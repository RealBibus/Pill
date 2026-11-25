param(
    [string]$PfxPath = ".\updates\pill-update.pfx",
    [Parameter(Mandatory = $true)][string]$PfxPassword,
    [string]$KeyOutputPath = ".\updates\PillUpdatePublicKey.txt"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function New-DerLength {
    param([int]$Length)

    if ($Length -lt 128) { return ,([byte]$Length) }

    $bytes = @()
    $value = $Length
    while ($value -gt 0) {
        $bytes = @([byte]($value -band 0xFF)) + $bytes
        $value = $value -shr 8
    }
    return @([byte](0x80 -bor $bytes.Count)) + $bytes
}

function New-DerValue {
    param([byte[]]$Tag, [byte[]]$Content)

    return [byte[]](@($Tag) + @(New-DerLength $Content.Length) + @($Content))
}

$directory = Split-Path -Parent $PfxPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
    'CN=Pill Update Signing',
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
)
$certificate = $request.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddDays(-1),
    [DateTimeOffset]::UtcNow.AddYears(10)
)
$pfx = $certificate.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
    $PfxPassword
)
$fullPfxPath = [System.IO.Path]::GetFullPath($PfxPath)
[System.IO.File]::WriteAllBytes($fullPfxPath, $pfx)

$algorithm = [byte[]](0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00)
$publicKey = [byte[]]$certificate.PublicKey.EncodedKeyValue.RawData
$bitString = [byte[]](@([byte]0x00) + @($publicKey))
$spkiBody = [byte[]](@($algorithm) + @(New-DerValue ([byte[]](0x03)) $bitString))
$spki = New-DerValue ([byte[]](0x30)) $spkiBody
$spkiBase64 = [Convert]::ToBase64String([byte[]]$spki)

$keyDirectory = Split-Path -Parent $KeyOutputPath
if (-not [string]::IsNullOrWhiteSpace($keyDirectory)) {
    New-Item -ItemType Directory -Path $keyDirectory -Force | Out-Null
}
Set-Content -LiteralPath $KeyOutputPath -Value $spkiBase64 -Encoding ASCII

"Private key: $PfxPath"
"Public key: $KeyOutputPath"
"Replace UpdateConfig.PublicKeySpki in Pill\Services\UpdateService.cs with the value below:"
$spkiBase64
