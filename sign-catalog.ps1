param(
    [Parameter(Mandatory=$true)][ValidateSet('Init','Sign')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$KeyFile,
    [string]$PublicKeyFile,
    [string]$CatalogFile,
    [string]$OutputFile,
    [string]$PreserveLauncherFrom
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$encoding = [Text.UTF8Encoding]::new($false)
$entropy = [Text.Encoding]::UTF8.GetBytes('FZO Launcher catalog signing v1')
$fullKey = [IO.Path]::GetFullPath($KeyFile)
if ($fullKey -match '(?i)[\\/](Sites|assets|dist|public-source|\.git)[\\/]') { throw 'The private key must be outside website, source and build directories.' }
function Write-Atomic($path, [string]$text) {
    $path = [IO.Path]::GetFullPath($path)
    $temporary = $path + '.tmp'
    [IO.File]::WriteAllText($temporary, $text, $encoding)
    if ([IO.File]::Exists($path)) {
        # PowerShell 7 can bind $null as an empty string for this .NET overload.
        # A concrete backup path keeps replacement atomic on every publication.
        $backup = $path + '.previous'
        [IO.File]::Replace($temporary, $path, $backup)
        [IO.File]::Delete($backup)
    }
    else { [IO.File]::Move($temporary, $path) }
}
if ($Mode -eq 'Init') {
    if ([IO.File]::Exists($fullKey)) { throw 'Refusing to replace an existing private key.' }
    if (-not $PublicKeyFile) { throw 'PublicKeyFile is required.' }
    $keyDirectory = [IO.Path]::GetDirectoryName($fullKey)
    New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($identity)
    foreach ($sid in @($identity, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $keyDirectory -AclObject $acl
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
    $rsa.PersistKeyInCsp = $false
    try {
        $secret = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        try {
            $protected = [Security.Cryptography.ProtectedData]::Protect($secret, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($fullKey, $protected)
        } finally { [Array]::Clear($secret, 0, $secret.Length) }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($PublicKeyFile), $rsa.ToXmlString($false), $encoding)
    } finally { $rsa.Dispose() }
    Write-Output 'Created a 3072-bit update key. Private key is DPAPI-protected for this Windows user; only public key is exported.'
    exit 0
}
if (-not $CatalogFile -or -not $OutputFile) { throw 'CatalogFile and OutputFile are required.' }
$lockPath = $fullKey + '.lock'
$keyLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $secret = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($fullKey), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
    $rsa.PersistKeyInCsp = $false
    try {
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($secret))
        if ($rsa.KeySize -ne 3072) { throw 'Unexpected key size.' }
        $catalog = [IO.File]::ReadAllText([IO.Path]::GetFullPath($CatalogFile)) | ConvertFrom-Json
        if ($catalog.schema -ne 1 -or $catalog.servers.Count -ne 2) { throw 'Invalid catalog.' }
        if ($PreserveLauncherFrom) {
            # Never re-sign launcher metadata taken from an unauthenticated website file.
            $previous = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PreserveLauncherFrom)) | ConvertFrom-Json
            if ($previous.schema -ne 1 -or $previous.algorithm -ne 'RSA-SHA256') { throw 'Invalid previous catalog.' }
            $previousPayload = [Convert]::FromBase64String($previous.payload)
            if (-not $rsa.VerifyData($previousPayload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'), [Convert]::FromBase64String($previous.signature))) {
                throw 'Previous catalog signature is invalid; refusing to preserve launcher metadata.'
            }
            $previousCatalog = [Text.Encoding]::UTF8.GetString($previousPayload) | ConvertFrom-Json
            if ($previousCatalog.launcher) { $catalog | Add-Member -NotePropertyName launcher -NotePropertyValue $previousCatalog.launcher -Force }
        }
        $statePath = $fullKey + '.state.json'
        $sequence = 1L
        if (Test-Path -LiteralPath $statePath) { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json; $sequence = [long]$state.sequence + 1 }
        $now = [long][Math]::Floor(([DateTime]::UtcNow - [DateTime]::new(1970,1,1)).TotalSeconds)
        $catalog | Add-Member -NotePropertyName sequence -NotePropertyValue $sequence -Force
        $catalog | Add-Member -NotePropertyName issuedAt -NotePropertyValue $now -Force
        $catalog | Add-Member -NotePropertyName expiresAt -NotePropertyValue ($now + 180L*86400) -Force
        $payload = [Text.Encoding]::UTF8.GetBytes(($catalog | ConvertTo-Json -Depth 10 -Compress))
        $signature = $rsa.SignData($payload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        if (-not $rsa.VerifyData($payload, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'), $signature)) { throw 'Signature verification failed.' }
        $envelope = [ordered]@{schema=1;algorithm='RSA-SHA256';payload=[Convert]::ToBase64String($payload);signature=[Convert]::ToBase64String($signature)}
        # Reserve the sequence before publishing. A failed publication may skip a sequence, never reuse it.
        Write-Atomic $statePath (@{sequence=$sequence} | ConvertTo-Json -Compress)
        Write-Atomic $OutputFile ($envelope | ConvertTo-Json -Compress)
        Write-Output "Catalog signed: sequence $sequence, valid for 180 days."
    } finally { [Array]::Clear($secret,0,$secret.Length); $rsa.Dispose() }
} finally { $keyLock.Dispose() }
