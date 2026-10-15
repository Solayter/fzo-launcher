$ErrorActionPreference = 'Stop'
$javaHome = $env:FZO_JAVA_HOME
if (-not $javaHome) { $javaHome = $env:JAVA_HOME_8_X64 }
if (-not $javaHome) { $javaHome = $env:JAVA_HOME }
if (-not $javaHome) { throw 'Set FZO_JAVA_HOME to a JDK 7/8 installation (CI uses JAVA_HOME_8_X64).' }
$javac = Join-Path $javaHome 'bin\javac.exe'
$jarTool = Join-Path $javaHome 'bin\jar.exe'
if (-not (Test-Path -LiteralPath $javac) -or -not (Test-Path -LiteralPath $jarTool)) { throw 'A JDK 7/8 with javac and jar is required to build the original Java icon helper.' }
$classes = Join-Path $PSScriptRoot ('dist\bootstrap-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $classes -Force | Out-Null
try {
    & $javac -encoding UTF-8 -source 1.6 -target 1.6 -d $classes (Join-Path $PSScriptRoot 'java\fzo\launcher\ClientBootstrap.java')
    if ($LASTEXITCODE -ne 0) { throw 'Java bootstrap compilation failed; use JDK 7/8.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\neuro-cube.png') -Destination $classes
    & $jarTool cf (Join-Path $PSScriptRoot 'dist\game-bootstrap.jar') -C $classes .
    if ($LASTEXITCODE -ne 0) { throw 'Java bootstrap packaging failed.' }
} finally {
    $resolvedClasses = [IO.Path]::GetFullPath($classes)
    $expectedDist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist')).TrimEnd('\') + '\'
    if ($resolvedClasses.StartsWith($expectedDist, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedClasses) -match '^bootstrap-[a-f0-9]{32}$') {
        [IO.Directory]::Delete($resolvedClasses, $true)
    }
}
