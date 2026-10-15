param([string]$PublicRoot, [string]$ClientRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assembly = Join-Path $projectRoot 'dist\FZO-Launcher.exe'
if (-not (Test-Path -LiteralPath $assembly)) { & (Join-Path $projectRoot 'build.ps1') }
$executable = Join-Path $projectRoot 'dist\ReleaseSmoke.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /utf8output /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll ("/r:" + $assembly) ("/out:" + $executable) (Join-Path $PSScriptRoot 'ReleaseSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed.' }
if ($PublicRoot) {
    if (-not $ClientRoot) { throw 'ClientRoot is required for a local full-install test.' }
    & $executable $PublicRoot $ClientRoot
} else {
    & $executable remote (Join-Path $projectRoot 'dist\remote-validation') $assembly
}
if ($LASTEXITCODE -ne 0) { throw 'Release smoke test failed.' }
