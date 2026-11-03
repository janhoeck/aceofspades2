<#
.SYNOPSIS
    Holt LiteNetLib in einer bestimmten Version als eingebettetes Unity-Paket nach client/Packages/com.revenantx.litenetlib.

.DESCRIPTION
    Unity bekommt LiteNetLib als Quellcode (so empfiehlt es der Autor). Das Paket liegt eingebettet im Projekt:
    Unity 6 warnt bei Paketen aus fremden Registries (OpenUPM) bei jedem Start, weil sie nicht signiert sind,
    und ein Git-Link geht nicht, weil im LiteNetLib-Repository die .meta-Dateien fehlen.

    Das Skript lädt das Paket von OpenUPM (dort mit .meta-Dateien, damit die GUIDs stabil bleiben), vergleicht
    die Quelldateien mit dem offiziellen GitHub-Tag derselben Version und ersetzt den Paketordner nur, wenn
    alles übereinstimmt.

    Danach dieselbe Version in libs/AoS2.Net/AoS2.Net.csproj und server/AoS2.Server/AoS2.Server.csproj eintragen.

.EXAMPLE
    ./tools/dev/update-litenetlib.ps1 -Version 2.1.4
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$target = Join-Path $root 'client\Packages\com.revenantx.litenetlib'
$work = Join-Path $root "artifacts\litenetlib-$Version"

if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

Write-Host "Lade LiteNetLib $Version von OpenUPM und GitHub ..."
Invoke-WebRequest "https://package.openupm.com/com.revenantx.litenetlib/-/com.revenantx.litenetlib-$Version.tgz" -OutFile "$work\upm.tgz"
Invoke-WebRequest "https://github.com/RevenantX/LiteNetLib/archive/refs/tags/$Version.zip" -OutFile "$work\github.zip"
tar -xzf "$work\upm.tgz" -C $work
Expand-Archive "$work\github.zip" -DestinationPath "$work\github"

$upm = Join-Path $work 'package'
$github = Join-Path $work "github\LiteNetLib-$Version\LiteNetLib"

# Jede .cs-Datei muss Byte für Byte (bis auf Zeilenenden) dem offiziellen Quellcode entsprechen.
$upmFiles = Get-ChildItem $upm -Recurse -Filter *.cs | ForEach-Object { $_.FullName.Substring($upm.Length + 1) } | Sort-Object
$githubFiles = Get-ChildItem $github -Recurse -Filter *.cs | ForEach-Object { $_.FullName.Substring($github.Length + 1) } | Sort-Object
if (Compare-Object $upmFiles $githubFiles) { throw 'Die Dateilisten von OpenUPM und GitHub unterscheiden sich.' }
foreach ($file in $upmFiles) {
    $a = (Get-Content -Raw (Join-Path $upm $file)) -replace "`r`n", "`n"
    $b = (Get-Content -Raw (Join-Path $github $file)) -replace "`r`n", "`n"
    if ($a -ne $b) { throw "$file unterscheidet sich zwischen OpenUPM und GitHub." }
}

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Copy-Item -Recurse $upm $target
Remove-Item -Force (Join-Path $target 'LiteNetLib.csproj*')
Copy-Item (Join-Path $work "github\LiteNetLib-$Version\LICENSE.txt") (Join-Path $target 'LICENSE.txt')
Remove-Item -Recurse -Force $work

Write-Host "LiteNetLib $Version liegt jetzt in client/Packages/com.revenantx.litenetlib."
Write-Host 'Nicht vergessen: dieselbe Version in libs/AoS2.Net/AoS2.Net.csproj und server/AoS2.Server/AoS2.Server.csproj eintragen.'
