<#
.SYNOPSIS
    Baut den Spielserver als Download für Self-Hoster: self-contained (keine .NET-Installation nötig), eine Datei
    pro Plattform.

.DESCRIPTION
    Ergebnis in artifacts/release/server/:
      AoS2.Server-<v>-win-x64.zip
      AoS2.Server-<v>-linux-x64.tar.gz
      AoS2.Server-<v>-linux-arm64.tar.gz

    Inhalt jedes Archivs (Ordner AoS2.Server/):
      AoS2.Server(.exe)       der Server (eine Datei mit eingebauter .NET-Laufzeit)
      server.example.json     Beispiel-Config, nach server.json kopieren und anpassen
      data/, maps/            Spieldaten und Maps
      README.txt              die ersten Schritte, Anleitung: docs/self-hosting.md

    Linux-Archive als .tar.gz, damit das Ausführungsrecht erhalten bleibt. Dafür muss tar vorhanden sein
    (unter Windows 10/11 und Linux eingebaut).

.EXAMPLE
    ./tools/release/pack-server.ps1 -Version 0.7.0
    ./tools/release/pack-server.ps1 -Version 0.7.0 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string[]]$Runtime = @('win-x64', 'linux-x64', 'linux-arm64'),
    [string]$ReleaseDir
)

. (Join-Path $PSScriptRoot 'common.ps1')

$Version = ConvertTo-ReleaseVersion $Version
if (-not $ReleaseDir) { $ReleaseDir = $DefaultReleaseDir }
$serverDir = Join-Path $ReleaseDir 'server'
New-Item -ItemType Directory -Force $serverDir | Out-Null

# Maps liegen in Git LFS; ohne LFS stehen im Repo nur Verweis-Dateien, mit denen der Server nicht startet.
foreach ($map in Get-ChildItem (Join-Path $RepoRoot 'maps') -Filter '*.aos2map') {
    $head = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($map.FullName), 0, [Math]::Min(7, $map.Length))
    if ($head -ne 'AOS2MAP') { throw "$($map.Name) ist nur ein Git-LFS-Verweis. Vorher 'git lfs pull' ausführen." }
}

foreach ($rid in $Runtime) {
    $name = "AoS2.Server-$Version-$rid"
    $staging = Join-Path $RepoRoot "artifacts/server-publish/$rid"
    $publishDir = Join-Path $staging 'AoS2.Server'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

    Write-Host "Veröffentliche Server $Version für $rid …"
    # Eine Datei mit eingebauter, komprimierter Laufzeit; data/, maps/ und die Beispiel-Config liegen daneben.
    Invoke-Checked {
        dotnet publish (Join-Path $RepoRoot 'server/AoS2.Server/AoS2.Server.csproj') `
            -c Release -r $rid --self-contained -o $publishDir `
            "-p:Version=$Version" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
    }

    $exe = if ($rid -like 'win-*') { 'AoS2.Server.exe' } else { './AoS2.Server' }
    $readme = @"
Ace of Spades 2 – Spielserver $Version ($rid)

1. server.example.json nach server.json kopieren und anpassen (name, address, site).
2. Den UDP-Port (Standard 32887) im Router an diesen Rechner weiterleiten und in der Firewall freigeben.
3. Starten:  $exe --config server.json

Nach dem Start steht im Log "Im Server-Browser eingetragen", sobald der Server von außen erreichbar ist.
Ausführliche Anleitung und Fehlersuche: https://github.com/janhoeck/aceofspades2/blob/main/docs/self-hosting.md
"@
    Set-Content -Path (Join-Path $publishDir 'README.txt') -Value $readme -Encoding utf8

    if ($rid -like 'win-*') {
        $archive = Join-Path $serverDir "$name.zip"
        if (Test-Path $archive) { Remove-Item $archive }
        Compress-Archive -Path $publishDir -DestinationPath $archive
    }
    else {
        $archive = Join-Path $serverDir "$name.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive }
        # Unter Windows kennt das Dateisystem kein Ausführungsrecht; tar setzt es dann nicht. Auf dem Linux-Runner der CI schon.
        if ([Environment]::OSVersion.Platform -eq 'Unix') { Invoke-Checked { chmod +x (Join-Path $publishDir 'AoS2.Server') } }
        Invoke-Checked { tar -czf $archive -C $staging 'AoS2.Server' }
    }
    Write-Host "  → $archive ($([Math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
}
