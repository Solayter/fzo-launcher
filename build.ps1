$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $launcherRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', '/langversion:5',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll',
    ('/win32manifest:' + (Join-Path $launcherRoot 'app.manifest')), ('/win32icon:' + (Join-Path $launcherRoot 'assets\owl.ico')), ('/resource:' + (Join-Path $launcherRoot 'assets\owl.png') + ',owl.png'),
    ('/resource:' + (Join-Path $launcherRoot 'assets\owl.ico') + ',owl.ico'), ('/resource:' + (Join-Path $launcherRoot 'assets\update-public-key.xml') + ',update-public-key.xml'), ('/out:' + (Join-Path $output 'FZO-Launcher.exe')),
    (Join-Path $launcherRoot 'SignedCatalog.cs'), (Join-Path $launcherRoot 'Updater.cs'), (Join-Path $launcherRoot 'Launcher.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
Copy-Item -LiteralPath (Join-Path $launcherRoot 'FZO-Launcher.exe.config') -Destination $output -Force
Write-Output (Join-Path $output 'FZO-Launcher.exe')
