# Ace of Spades 2

> Arbeitstitel

Ein Team-Shooter (Blau gegen Grün) in einer vollständig zerstörbaren, blockartigen Welt. Gebaut mit Unity und einem eigenen .NET-Spielserver.

## Dokumentation

- [Game Design](docs/game-design.md) – was das Spiel ist und welche Regeln gelten
- [Architektur](docs/architecture.md) – wie Client, Server, Backend und Launcher zusammenspielen
- [Assets](docs/assets.md) – Stil, Herstellung und Liste aller Grafiken, Modelle und Sounds
- [Roadmap](docs/roadmap.md) – Meilensteine
- [Maps bauen](docs/maps.md) – MagicaVoxel, Zuordnungsdatei, Metadaten, Map-Konverter
- [Releases und Updates](docs/release.md) – Launcher, Update-Speicher (Cloudflare R2), Release-Workflow
- [Website, Backend und Discord-Login](docs/backend.md) – Anmeldung, lokal entwickeln, Discord-App, Coolify
- [Spielserver und Netzwerk](docs/server.md) – Server starten, Entwicklungsmodus, zwei Clients lokal verbinden

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
| `web/` | Website (Next.js): Startseite, Discord-Login, reicht `/api` an das Backend durch |
| `deploy/` | Deployment-Dateien für Coolify |
| `docs/` | Dokumentation |

## Voraussetzungen

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Unity 6 (über Unity Hub), Version siehe `client/ProjectSettings/ProjectVersion.txt`
- Git + Git LFS
- Für Website und Backend: Node.js 22 und PostgreSQL

## Bauen und testen

```bash
dotnet build AoS2.slnx
dotnet test AoS2.slnx
```

Alle Build-Ausgaben landen in `artifacts/`. Den Unity-Client öffnet man über Unity Hub → *Add* → Ordner `client/`.

## Lokal spielen (ohne Server)

Im Unity-Editor die Szene `Assets/Scenes/Game.unity` öffnen, *Play* drücken und im Startmenü **Offline spielen** wählen. Das Spiel lädt `maps/testmap.aos2map`; ins Spielfenster klicken fängt die Maus.

| Taste | Aktion |
|---|---|
| W A S D | laufen |
| Maus | umsehen |
| Leertaste | springen |
| Strg (links) | ducken |
| Shift (links) | sprinten |
| Linksklick | Block abbauen |
| Rechtsklick | Block bauen |
| 1–9, Mausrad | Baublock wählen |
| Esc | Maus freigeben (zweimal: zurück ins Startmenü) |

Blocktypen stehen in `data/blocks.json`, Bewegungswerte in `data/movement.json`; im Editor wirken Änderungen beim nächsten *Play*.

## Mit Server spielen (lokal, zwei Clients)

```powershell
dotnet run --project server/AoS2.Server -- --dev   # Server im Entwicklungsmodus, eigenes Terminal
```

Dann im Editor *Play* und im Startmenü mit `127.0.0.1:32887` und Haken bei **Entwicklungs-Ticket** verbinden; ein Windows-Build (*AoS2 → Windows-Build erstellen*) ist der zweite Client. Ausführlich: [docs/server.md](docs/server.md).

Launcher und Spiel lokal ausprobieren, mit lokaler Website, lokalem Backend und Entwicklungs-Login statt Discord (Details in [docs/backend.md](docs/backend.md#3-lokal-entwickeln) und [docs/release.md](docs/release.md#5-ohne-r2-lokal-testen)):

```powershell
./tools/dev/dev-db.ps1                       # Datenbank (PostgreSQL muss installiert sein)
dotnet run --project backend/AoS2.Backend    # Backend auf http://localhost:5080, in einem eigenen Terminal
cd web; npm install; npm run dev; cd ..      # Website auf http://localhost:3000, in einem eigenen Terminal
./tools/release/build-game.ps1 -Version 0.1.0
$env:AOS2_UPDATE_URL = "$PWD\artifacts\release"
$env:AOS2_SITE_URL = "http://localhost:3000"
dotnet run --project launcher/AoS2.Launcher
```
