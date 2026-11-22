param(
    [string]$OutputName = 'GGS-WiFi.exe',
    [ValidateSet('x64', 'x86', 'anycpu')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or [IO.Path]::GetExtension($OutputName) -ne '.exe') {
    throw 'OutputName must be an .exe filename without a directory.'
}
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires Windows .NET Framework 4.8.' }
$outputDir = Join-Path $projectRoot 'outputs'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Web.dll', '/r:System.Web.Extensions.dll')
$appIcon = Join-Path $projectRoot 'assets\app.ico'
if (-not (Test-Path -LiteralPath $appIcon)) { throw 'Missing assets/app.ico.' }
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/platform:$Architecture" '/main:GgsWifi.Program' "/win32manifest:$projectRoot\app.manifest" "/win32icon:$appIcon" "/resource:$appIcon,GgsWifi.AppIcon" "/out:$outputDir\$OutputName" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputDir '使用说明.md') -Force
Write-Output "Built: $outputDir\$OutputName"
