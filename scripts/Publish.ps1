[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$')]
    [string]$Version,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'fast-torrent-download-win-x64'
$zipPath = Join-Path $artifactsRoot "fast-torrent-download-$Version-win-x64-portable.zip"
$checksumPath = "$zipPath.sha256"

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
& dotnet publish (Join-Path $projectRoot 'src\\FastTorrentDownload\\FastTorrentDownload.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:Version=$Version `
    --output $publishRoot

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath
"$((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)" |
    Set-Content -LiteralPath $checksumPath -NoNewline

if (-not $SkipInstaller) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -eq $iscc) {
        throw 'Inno Setup 6 is required for the installer. Install it or rerun with -SkipInstaller for the portable ZIP only.'
    }

    & $iscc.Source "/DMyAppVersion=$Version" "/DSourceDir=$publishRoot" "/DOutputDir=$artifactsRoot" `
        (Join-Path $projectRoot 'installer\\fast-torrent-download.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Created release artifacts in $artifactsRoot"
