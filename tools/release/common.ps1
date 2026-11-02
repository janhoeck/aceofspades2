# Gemeinsame Hilfsfunktionen der Release-Skripte. Wird per '. $PSScriptRoot/common.ps1' eingebunden.
# Läuft unter Windows PowerShell 5.1 und PowerShell 7 (auch unter Linux in der CI).

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$DefaultReleaseDir = Join-Path $RepoRoot 'artifacts/release'

# Führt ein externes Programm aus und bricht ab, wenn es mit einem Fehlercode endet.
# (PowerShell selbst bricht bei Fehlercodes externer Programme nicht ab.)
function Invoke-Checked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "Befehl fehlgeschlagen (Code $LASTEXITCODE): $Command"
    }
}

# 'v0.1.0' → '0.1.0', prüft die SemVer-Form.
function ConvertTo-ReleaseVersion([string]$Version) {
    $v = $Version.TrimStart('v')
    if ($v -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
        throw "'$Version' ist keine gültige Version (erwartet z. B. 0.1.0 oder v0.1.0)."
    }
    return $v
}

# Zugangsdaten für Cloudflare R2 kommen immer aus Umgebungsvariablen, nie aus Dateien im Repo.
function Assert-R2Environment {
    foreach ($name in 'R2_ACCOUNT_ID', 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_BUCKET') {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Umgebungsvariable $name fehlt. Siehe docs/release.md."
        }
    }
}

function Get-R2Endpoint {
    return "https://$($env:R2_ACCOUNT_ID).r2.cloudflarestorage.com"
}
