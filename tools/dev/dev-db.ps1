<#
.SYNOPSIS
    Startet eine PostgreSQL-Datenbank nur zum Entwickeln: artifacts/dev-db, Port 5433, ohne Passwort.

.DESCRIPTION
    Beim ersten Aufruf legt initdb einen eigenen Datenbank-Cluster in artifacts/dev-db an. Er lauscht nur
    auf 127.0.0.1:5433 und lässt den Benutzer aos2 ohne Passwort herein (Methode "trust"). Die
    installierte PostgreSQL (Port 5432) und ihre Passwörter bleiben unberührt.

    Das Backend-Profil "dev-login" (backend/AoS2.Backend/Properties/launchSettings.json) nutzt genau
    diese Datenbank. Die Tabellen legt das Backend beim Start selbst an (EF-Core-Migrationen).

.EXAMPLE
    ./tools/dev/dev-db.ps1          # starten (und beim ersten Mal anlegen)

.EXAMPLE
    ./tools/dev/dev-db.ps1 -Stop    # anhalten

.EXAMPLE
    ./tools/dev/dev-db.ps1 -Reset   # anhalten und löschen, alle Konten sind weg
#>
[CmdletBinding()]
param(
    [switch]$Stop,
    [switch]$Reset,
    [int]$Port = 5433
)

. (Join-Path $PSScriptRoot '../release/common.ps1')

$DataDir = Join-Path $RepoRoot 'artifacts/dev-db'
$LogFile = Join-Path $RepoRoot 'artifacts/logs/dev-db.log'

function Find-PgTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    # Der PostgreSQL-Installer trägt sich nicht immer in den PATH ein.
    $installed = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [int]$_.Parent.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "$Name.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if ($installed) { return $installed }
    throw "$Name nicht gefunden. PostgreSQL installieren: https://www.postgresql.org/download/windows/"
}

$pgCtl = Find-PgTool 'pg_ctl'
$running = (Test-Path $DataDir) -and ((& $pgCtl status -D $DataDir *>&1) -match 'PID')

if ($Stop -or $Reset) {
    if ($running) {
        Invoke-Checked { & $pgCtl stop -D $DataDir -m fast }
    }
    if ($Reset -and (Test-Path $DataDir)) {
        Remove-Item $DataDir -Recurse -Force
        Write-Host "Entwicklungs-Datenbank gelöscht."
    }
    return
}

if (-not (Test-Path $DataDir)) {
    Write-Host "Lege Entwicklungs-Datenbank in $DataDir an ..."
    $initdb = Find-PgTool 'initdb'
    Invoke-Checked { & $initdb -D $DataDir -U aos2 --auth=trust --encoding=UTF8 --no-locale }
}

if (-not $running) {
    New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
    # Direkt über Process.Start: Mit dem PowerShell-Aufruf (&) oder Start-Process -Wait würde dieses Skript
    # warten, bis die Datenbank wieder beendet ist, weil der Datenbankprozess die Ausgabe erbt.
    # (Leitet man die Ausgabe des Skripts selbst in eine Pipe um, hält die Datenbank diese Pipe offen.)
    $start = New-Object System.Diagnostics.ProcessStartInfo $pgCtl, "start -D `"$DataDir`" -l `"$LogFile`" -w -o `"-p $Port -c listen_addresses=127.0.0.1`""
    $start.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "pg_ctl start fehlgeschlagen (Code $($process.ExitCode)), siehe $LogFile" }
}

$psql = Find-PgTool 'psql'
$exists = & $psql -h 127.0.0.1 -p $Port -U aos2 -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'aos2'"
if ($exists -ne '1') {
    Invoke-Checked { & $psql -h 127.0.0.1 -p $Port -U aos2 -d postgres -c 'CREATE DATABASE aos2' }
}

Write-Host "Entwicklungs-Datenbank läuft: Host=127.0.0.1;Port=$Port;Database=aos2;Username=aos2"
