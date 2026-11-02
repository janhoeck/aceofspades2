<#
.SYNOPSIS
    Lädt artifacts/release/game/ in den R2-Bucket hoch (braucht die AWS CLI).

.DESCRIPTION
    Reihenfolge ist wichtig: erst alle Dateien (objects/), dann das Manifest. So sieht kein
    Launcher eine neue Version, deren Dateien noch fehlen.

    Benötigte Umgebungsvariablen: R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET
#>
[CmdletBinding()]
param(
    [string]$ReleaseDir
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $ReleaseDir) { $ReleaseDir = $DefaultReleaseDir }
$game = Join-Path $ReleaseDir 'game'
if (-not (Test-Path (Join-Path $game 'manifest.json'))) {
    throw "Kein Manifest in $game. Zuerst build-game.ps1 ausführen."
}
if (-not (Get-Command aws -ErrorAction SilentlyContinue)) {
    throw "AWS CLI nicht gefunden. Installieren: winget install Amazon.AWSCLI"
}
Assert-R2Environment

# R2 spricht die S3-API. Die AWS CLI liest ihre Zugangsdaten aus diesen Variablen.
$env:AWS_ACCESS_KEY_ID = $env:R2_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:R2_SECRET_ACCESS_KEY
$env:AWS_DEFAULT_REGION = 'auto'
# Neuere AWS-CLI-Versionen senden standardmäßig Prüfsummen-Header, die R2 nicht unterstützt.
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
$env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'

$endpoint = Get-R2Endpoint
$target = "s3://$($env:R2_BUCKET)/game"

Write-Host "1/3 Dateien hochladen (vorhandene werden übersprungen) …"
# Objekte sind nach ihrem Inhalt benannt und ändern sich nie: dürfen ewig gecacht werden.
Invoke-Checked {
    aws s3 sync (Join-Path $game 'objects') "$target/objects" --endpoint-url $endpoint `
        --size-only --no-progress --cache-control 'public, max-age=31536000, immutable'
}

Write-Host "2/3 Archiv-Manifeste hochladen …"
Invoke-Checked {
    aws s3 sync (Join-Path $game 'manifests') "$target/manifests" --endpoint-url $endpoint `
        --no-progress --cache-control 'no-cache' --content-type 'application/json'
}

Write-Host "3/3 Aktuelles Manifest hochladen …"
Invoke-Checked {
    aws s3 cp (Join-Path $game 'manifest.json') "$target/manifest.json" --endpoint-url $endpoint `
        --no-progress --cache-control 'no-cache' --content-type 'application/json'
}

Write-Host "Fertig. Launcher sehen die neue Version ab sofort."
