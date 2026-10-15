$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $launcherRoot 'tests\UpdaterTests.exe'
& $compiler /nologo /target:exe /platform:x64 /langversion:5 /codepage:65001 /utf8output /main:Tests /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll ("/resource:" + (Join-Path $launcherRoot 'assets\update-public-key.xml') + ',update-public-key.xml') ("/out:" + $testExe) (Join-Path $launcherRoot 'GameBootstrap.cs') (Join-Path $launcherRoot 'LauncherUpdate.cs') (Join-Path $launcherRoot 'SignedCatalog.cs') (Join-Path $launcherRoot 'Updater.cs') (Join-Path $launcherRoot 'Launcher.cs') (Join-Path $launcherRoot 'tests\UpdaterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$fixtureFolder = Join-Path $launcherRoot 'dist'
New-Item -ItemType Directory -Path $fixtureFolder -Force | Out-Null
$fixtureSource = Join-Path $fixtureFolder 'OldLauncher.cs'
[IO.File]::WriteAllText($fixtureSource, '[assembly: System.Reflection.AssemblyVersion("1.1.0.0")] class OldLauncher { static void Main() {} }')
& $compiler /nologo /target:winexe /platform:x64 ("/out:" + (Join-Path $fixtureFolder 'OldLauncher.exe')) $fixtureSource
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
& $testExe (Join-Path $launcherRoot 'tests')
if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed.' }

# Exercise repeated publication, not just the first write of an absent catalog.
$signTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('fzo-signer-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $signTestRoot | Out-Null
try {
    $testKey = Join-Path $signTestRoot 'test.dpapi'
    $testPublic = Join-Path $signTestRoot 'public.xml'
    $testInput = Join-Path $signTestRoot 'input.json'
    $testOutput = Join-Path $signTestRoot 'catalog.signed.json'
    $signer = Join-Path $launcherRoot 'sign-catalog.ps1'
    $signShell = (Get-Process -Id $PID).Path
    & $signShell -NoProfile -NonInteractive -File $signer -Mode Init -KeyFile $testKey -PublicKeyFile $testPublic
    if ($LASTEXITCODE -ne 0) { throw 'Test key initialization failed.' }
    $metadata = @{version='1.2.0.0';sha256=('a' * 64);size=4096}
    @{schema=1;servers=@(@{id='beta'},@{id='industrial'});launcher=$metadata} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testInput -Encoding utf8
    & $signShell -NoProfile -NonInteractive -File $signer -Mode Sign -KeyFile $testKey -CatalogFile $testInput -OutputFile $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'First test catalog signature failed.' }
    @{schema=1;servers=@(@{id='beta'},@{id='industrial'})} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testInput -Encoding utf8
    & $signShell -NoProfile -NonInteractive -File $signer -Mode Sign -KeyFile $testKey -CatalogFile $testInput -OutputFile $testOutput -PreserveLauncherFrom $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Repeated catalog publication failed.' }
    $signed = Get-Content -LiteralPath $testOutput -Raw | ConvertFrom-Json
    $signedPayload = [Convert]::FromBase64String($signed.payload)
    $parsed = [Text.Encoding]::UTF8.GetString($signedPayload) | ConvertFrom-Json
    $testRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    $testRsa.PersistKeyInCsp = $false
    try {
        $testRsa.FromXmlString([IO.File]::ReadAllText($testPublic))
        if (-not $testRsa.VerifyData($signedPayload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'), [Convert]::FromBase64String($signed.signature))) { throw 'Repeated signature does not verify.' }
    } finally { $testRsa.Dispose() }
    if ($parsed.sequence -ne 2 -or $parsed.launcher.sha256 -ne $metadata.sha256 -or $parsed.launcher.version -ne '1.2.0.0') { throw 'Repeated signing lost launcher metadata or sequence.' }
    Write-Output 'PASS: repeated atomic signing, increasing sequence, preserved authenticated launcher metadata.'
} finally {
    # Only the exact, unique test directory under the OS temp directory is removed.
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($signTestRoot)) -eq $expectedParent -and [IO.Path]::GetFileName($signTestRoot) -match '^fzo-signer-test-[a-f0-9]{32}$') {
        [IO.Directory]::Delete($signTestRoot, $true)
    }
}
