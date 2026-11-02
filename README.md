# Ace of Spades 2

> Arbeitstitel

Ein Team-Shooter (Blau gegen Grün) in einer vollständig zerstörbaren, blockartigen Welt. Gebaut mit Unity und einem eigenen .NET-Spielserver.

## Dokumentation

- [Game Design](docs/game-design.md) – was das Spiel ist und welche Regeln gelten
- [Architektur](docs/architecture.md) – wie Client, Server, Backend und Launcher zusammenspielen
- [Assets](docs/assets.md) – Stil, Herstellung und Liste aller Grafiken, Modelle und Sounds
- [Roadmap](docs/roadmap.md) – Meilensteine
- [Releases und Updates](docs/release.md) – Launcher, Update-Speicher (Cloudflare R2), Release-Workflow

## Aufbau des Repositories

| Ordner | Inhalt |
|---|---|
| `client/` | Unity-Projekt |
| `server/` | Dedizierter Spielserver (.NET) |
| `backend/` | Web-API: Discord-Login, Accounts, Server-Browser |
| `launcher/` | Launcher mit Auto-Update |
| `libs/` | Gemeinsamer Code (Client + Server) |
| `tools/` | Map-Konverter, Manifest-Builder |
| `data/` | Blöcke, Klassen, Waffen als JSON |
| `maps/` | Maps |
| `assets-src/` | Quelldateien der Assets (Texturen, Modelle, Sounds) |
| `deploy/` | Deployment-Dateien für Coolify |
| `docs/` | Dokumentation |

## Voraussetzungen

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Unity 6 (über Unity Hub), Version siehe `client/ProjectSettings/ProjectVersion.txt`
- Git + Git LFS

## Bauen und testen

```bash
dotnet build AoS2.slnx
dotnet test AoS2.slnx
```

Alle Build-Ausgaben landen in `artifacts/`. Den Unity-Client öffnet man über Unity Hub → *Add* → Ordner `client/`.

Launcher und Spiel lokal ausprobieren, ohne Server (Details in [docs/release.md](docs/release.md#5-ohne-r2-lokal-testen)):

```powershell
./tools/release/build-game.ps1 -Version 0.1.0
$env:AOS2_UPDATE_URL = "$PWD\artifacts\release"
dotnet run --project launcher/AoS2.Launcher
```
