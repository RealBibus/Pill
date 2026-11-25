param(
    [string]$Version = "1.0.0",
    [string]$Channel = "beta",
    [int]$RolloutPercent = 100,
    [string]$PfxPath = "",
    [string]$PfxPassword = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'Pill\Pill.csproj'
$installerProject = Join-Path $root 'Pill.Installer\Pill.Installer.csproj'
$payload = Join-Path $root 'Release\payload'
$releaseDir = Join-Path $root 'Release'
$notesPath = Join-Path $root 'RELEASE_NOTES.md'
$manifestPath = Join-Path $payload 'manifest.json'
$signaturePath = Join-Path $payload 'manifest.sig'
$zip = Join-Path $releaseDir "Pill-$Version-$Channel.zip"

Remove-Item -LiteralPath $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained false -p:Version=$Version -o $payload
dotnet publish $installerProject -c Release -r win-x64 --self-contained false -p:Version=$Version -o $payload

$notes = ""
if (Test-Path -LiteralPath $notesPath) {
    $notes = [string](Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8)
}

$files = @()
Get-ChildItem -LiteralPath $payload -File | Where-Object {
    $_.Name -ne 'manifest.json' -and
    $_.Name -ne 'manifest.sig' -and
    $_.Extension -ne '.zip' -and
    $_.Extension -ne '.dl' -and
    $_.Extension -ne '.tmp'
} | Sort-Object Name | ForEach-Object {
    $files += [ordered]@{
        path = $_.Name
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        size = $_.Length
        asset = $_.Name
    }
}

$manifest = [ordered]@{
    version = $Version
    channel = $Channel
    rollout_percent = $RolloutPercent
    release_notes = $notes
    entry = 'Pill.exe'
    files = @($files)
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    & (Join-Path $root 'sign-update.ps1') -ManifestPath $manifestPath -OutputPath $signaturePath -PfxPath $PfxPath -PfxPassword $PfxPassword
}

Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force

"Payload: $payload"
"Archive: $zip"
"Manifest: $manifestPath"
if (Test-Path -LiteralPath $signaturePath) { "Signature: $signaturePath" }
else { "Signature missing. Run sign-update.ps1 before publishing the release." }
