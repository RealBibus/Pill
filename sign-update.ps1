param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [string]$OutputPath = "",
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [string]$PfxPassword = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $ManifestPath) 'manifest.sig'
}

$data = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath))
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
    (Resolve-Path -LiteralPath $PfxPath),
    $PfxPassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
)

$rsa = $null
try {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
}
catch { }
if ($null -eq $rsa) {
    try { $rsa = [System.Security.Cryptography.RSACryptoServiceProvider]$cert.PrivateKey }
    catch { }
}
if ($null -eq $rsa) { throw 'The certificate has no RSA private key.' }

try {
    $signature = $rsa.SignData(
        $data,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
    )
}
catch {
    $signature = $rsa.SignData($data, 'SHA256')
}

[System.IO.File]::WriteAllBytes($OutputPath, $signature)
"Signed: $OutputPath"
