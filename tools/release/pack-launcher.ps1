<#
.SYNOPSIS
    Baut den Launcher, paketiert ihn mit Velopack und lädt ihn auf Wunsch nach R2 hoch.

.DESCRIPTION
    Ergebnis in artifacts/release/launcher/:
      AoS2.Launcher-win-Setup.exe     Installer für neue Spieler
      AoS2.Launcher-<v>-full.nupkg    Vollpaket für Self-Updates
      AoS2.Launcher-<v>-delta.nupkg   Nur die Änderungen zur Vorversion (ab dem 2. Release)
      releases.win.json               Liste der Releases; hier schaut der Launcher nach Updates

    -UpdateUrl wird fest in den Launcher eingebaut: Dort sucht er später Spiel und Launcher-Updates.
    Zum lokalen Testen kann das auch ein Ordner sein, z. B. der volle Pfad zu artifacts/release.

    -SiteUrl ist die Adresse der Website (Discord-Login, API unter /api), ebenfalls fest eingebaut. Ohne sie zeigt der
    Launcher "Kein Anmeldeserver eingestellt"; zum Testen hilft dann die Umgebungsvariable AOS2_SITE_URL.

.EXAMPLE
    ./tools/release/pack-launcher.ps1 -Version 0.1.0 -UpdateUrl https://updates.example.com -SiteUrl https://aos2.example.com -Upload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$UpdateUrl,
    [string]$SiteUrl = '',
    [string]$ReleaseDir,
    [switch]$Upload
)

. (Join-Path $PSScriptRoot 'common.ps1')

$Version = ConvertTo-ReleaseVersion $Version
$UpdateUrl = $UpdateUrl.TrimEnd('/', '\')
$SiteUrl = $SiteUrl.TrimEnd('/')
if ($Upload -and $SiteUrl -notmatch '^https://') { throw "-Upload braucht eine https-SiteUrl." }
if (-not $ReleaseDir) { $ReleaseDir = $DefaultReleaseDir }
$launcherDir = Join-Path $ReleaseDir 'launcher'
$publishDir = Join-Path $RepoRoot 'artifacts/launcher-publish'
$isHttp = $UpdateUrl -match '^https?://'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Veröffentliche Launcher $Version (Update-Speicher: $UpdateUrl, Website: $SiteUrl) …"
# Self-contained: Spieler brauchen keine .NET-Installation.
Invoke-Checked {
    dotnet publish (Join-Path $RepoRoot 'launcher/AoS2.Launcher/AoS2.Launcher.csproj') `
        -c Release -r win-x64 --self-contained -o $publishDir `
        "-p:Version=$Version" "-p:AoS2UpdateUrl=$UpdateUrl" "-p:AoS2SiteUrl=$SiteUrl"
}

Invoke-Checked { dotnet tool restore }

if ($isHttp) {
    # Das letzte veröffentlichte Release holen, damit vpk ein kleines Delta-Paket bauen kann.
    Write-Host "Lade vorheriges Launcher-Release …"
    Invoke-Checked {
        dotnet vpk download http --url "$UpdateUrl/launcher" --outputDir $launcherDir --allowEmptyChannel
    }
}

Write-Host "Paketiere mit Velopack …"
Invoke-Checked {
    dotnet vpk pack --packId AoS2.Launcher --packVersion $Version --packDir $publishDir `
        --mainExe AoS2.Launcher.exe --packTitle 'Ace of Spades 2' --packAuthors 'Ace of Spades 2' `
        --runtime win-x64 --outputDir $launcherDir
}

if ($Upload) {
    if (-not $isHttp) { throw "-Upload braucht eine http(s)-UpdateUrl." }
    Assert-R2Environment
    Write-Host "Lade Launcher nach R2 hoch …"
    # vpk lädt erst die Pakete und zuletzt releases.win.json hoch und räumt alte Releases auf.
    Invoke-Checked {
        dotnet vpk upload s3 --outputDir $launcherDir --bucket $env:R2_BUCKET --prefix launcher `
            --endpoint (Get-R2Endpoint) --region auto `
            --keyId $env:R2_ACCESS_KEY_ID --secret $env:R2_SECRET_ACCESS_KEY `
            --disableChecksumValidation --keepMaxReleases 10
    }
}

Write-Host "Fertig: $launcherDir"
