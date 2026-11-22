$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testRoot = Join-Path $projectRoot 'test-results'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $projectRoot 'tests\Tests.cs'
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Security.dll', '/r:System.Web.dll', '/r:System.Web.Extensions.dll')
$appIcon = Join-Path $projectRoot 'assets\app.ico'
& $compiler /nologo /target:exe /codepage:65001 /platform:anycpu '/main:GgsWifi.Tests' "/win32manifest:$projectRoot\app.manifest" "/win32icon:$appIcon" "/resource:$appIcon,GgsWifi.AppIcon" "/out:$testRoot\Tests.exe" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $testRoot 'Tests.exe') $testRoot
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
