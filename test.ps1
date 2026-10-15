$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $launcherRoot 'tests\UpdaterTests.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /utf8output /main:Tests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll ("/resource:" + (Join-Path $launcherRoot 'assets\update-public-key.xml') + ',update-public-key.xml') ("/out:" + $testExe) (Join-Path $launcherRoot 'SignedCatalog.cs') (Join-Path $launcherRoot 'Updater.cs') (Join-Path $launcherRoot 'Launcher.cs') (Join-Path $launcherRoot 'tests\UpdaterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe (Join-Path $launcherRoot 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed.' }
