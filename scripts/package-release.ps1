param(
    [string]$Version = '',
    [ValidateSet('x64', 'x86', 'anycpu')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $assemblyInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AssemblyInfo.cs') -Raw
    $versionMatch = [regex]::Match($assemblyInfo, 'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.\d+"\)')
    if (-not $versionMatch.Success) { throw 'Cannot read application version.' }
    $Version = $versionMatch.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use MAJOR.MINOR.PATCH.' }
$baseName = "GGS-WiFi-v$Version-windows-$Architecture"
$exeName = "$baseName.exe"
& (Join-Path $projectRoot 'build.ps1') -OutputName $exeName -Architecture $Architecture
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$outputDir = Join-Path $projectRoot 'outputs'
$exePath = Join-Path $outputDir $exeName
$builtVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
if ($builtVersion -ne "$Version.0") { throw "Assembly version $builtVersion does not match release $Version." }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $outputDir "$baseName.zip"
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $exePath, 'GGS-WiFi.exe') | Out-Null
    foreach ($document in @('README.md', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $projectRoot $document), $document) | Out-Null
    }
    foreach ($resource in @('assets/app-icon.png', 'assets/README.md', 'docs/USAGE.md', 'docs/screenshot.png')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $projectRoot $resource), $resource) | Out-Null
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$hashLines = foreach ($file in @($exePath, $zipPath)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))"
}
[IO.File]::WriteAllLines((Join-Path $outputDir 'SHA256SUMS.txt'), $hashLines, [Text.UTF8Encoding]::new($false))
Write-Output "Release: $exePath"
Write-Output "Archive: $zipPath"
Write-Output "Checksums: $outputDir\SHA256SUMS.txt"
