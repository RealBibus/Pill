param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'Pill\Pill.csproj'
$publishDir = Join-Path $root 'Release\publish'
$releaseDir = Join-Path $root 'Release'
$zip = Join-Path $releaseDir "Pill-$Version-beta.zip"
Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publishDir
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -Force
"Release archive: $zip"
