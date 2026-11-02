<#
.SYNOPSIS
    Baut den Unity-Client, erzeugt das Update-Manifest und lädt es auf Wunsch nach R2 hoch.

.DESCRIPTION
    1. Unity im Batchmode: client/ → artifacts/unity-build/StandaloneWindows64/AoS2.exe
    2. AoS2.ManifestBuilder: → artifacts/release/game/ (manifest.json + objects/)
    3. Mit -Upload: artifacts/release/game/ → R2-Bucket (siehe upload-game.ps1)

    Ohne -Upload ist artifacts/release ein vollständiger lokaler Update-Speicher. Den kann man
    dem Launcher zum Testen geben: $env:AOS2_UPDATE_URL = "<Repo>\artifacts\release"

.EXAMPLE
    ./tools/release/build-game.ps1 -Version 0.1.0

.EXAMPLE
    ./tools/release/build-game.ps1 -Version 0.1.0 -Upload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    # Standard: die Unity-Version aus client/ProjectSettings/ProjectVersion.txt im Unity Hub.
    [string]$UnityPath,
    [string]$ReleaseDir,
    # Vorhandenen Build in artifacts/unity-build wiederverwenden.
    [switch]$SkipUnityBuild,
    [switch]$Upload
)

. (Join-Path $PSScriptRoot 'common.ps1')

$Version = ConvertTo-ReleaseVersion $Version
if (-not $ReleaseDir) { $ReleaseDir = $DefaultReleaseDir }
$buildRoot = Join-Path $RepoRoot 'artifacts/unity-build'
$buildDir = Join-Path $buildRoot 'StandaloneWindows64'

if (-not $SkipUnityBuild) {
    if (-not $UnityPath) {
        $projectVersion = Get-Content (Join-Path $RepoRoot 'client/ProjectSettings/ProjectVersion.txt') -Raw
        $editorVersion = [regex]::Match($projectVersion, 'm_EditorVersion: (\S+)').Groups[1].Value
        $UnityPath = Join-Path $env:ProgramFiles "Unity/Hub/Editor/$editorVersion/Editor/Unity.exe"
    }
    if (-not (Test-Path $UnityPath)) {
        throw "Unity nicht gefunden: $UnityPath (mit -UnityPath angeben)."
    }

    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    New-Item -ItemType Directory -Force $buildRoot | Out-Null
    $log = Join-Path $buildRoot 'build.log'

    Write-Host "Baue Unity-Client $Version … (Log: $log)"
    # Unity.exe ist eine Fensteranwendung; ohne Start-Process -Wait würde PowerShell nicht warten.
    $unity = Start-Process -FilePath $UnityPath -Wait -PassThru -NoNewWindow -ArgumentList @(
        '-batchmode', '-quit',
        '-projectPath', "`"$(Join-Path $RepoRoot 'client')`"",
        '-executeMethod', 'AoS2.Editor.BuildScript.BuildWindows',
        '-buildVersion', $Version,
        '-customBuildPath', "`"$(Join-Path $buildDir 'AoS2.exe')`"",
        '-logFile', "`"$log`""
    )
    if ($unity.ExitCode -ne 0) {
        throw "Unity-Build fehlgeschlagen (Code $($unity.ExitCode)). Details im Log: $log"
    }
}

Write-Host "Erzeuge Manifest …"
Invoke-Checked {
    dotnet run --project (Join-Path $RepoRoot 'tools/AoS2.ManifestBuilder') -c Release -- `
        --input $buildDir --output $ReleaseDir --version $Version
}

if ($Upload) {
    & (Join-Path $PSScriptRoot 'upload-game.ps1') -ReleaseDir $ReleaseDir
}
