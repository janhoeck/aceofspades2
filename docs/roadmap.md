# Ace of Spades 2 – Roadmap

Jeder Meilenstein endet mit etwas, das man ausprobieren kann. Die Reihenfolge stellt den Launcher bewusst früh: Er ist der Einstiegspunkt, und jede spätere Version kommt über ihn zu den Testern.

Assets laufen parallel zur Programmierung: Bis M5 reichen Platzhalter, ab M6 werden sie schrittweise durch fertige Assets ersetzt. Welche Assets in welchem Meilenstein gebraucht werden, steht in [assets.md](assets.md#6-asset-liste).

| # | Meilenstein | Fertig, wenn … |
|---|---|---|
| M0 | Fundament | das Repo baut und testet automatisch auf GitHub |
| M1 | Launcher & Update-Pipeline | ein frischer PC über den Launcher ein Platzhalter-Spiel installiert, aktualisiert und startet |
| M2 | Backend & Discord-Login | man sich im Launcher mit Discord anmeldet und das Spiel den Namen anzeigt |
| M3 | Voxel-Welt (lokal) | man über eine texturierte Map läuft, Blöcke abbaut und baut und schwebende Blöcke fallen |
| M4 | Netzwerk-Grundlage | zwei Spieler auf einem Server flüssig herumlaufen und Blöcke verändern |
| M5 | Kampf | Spieler sich mit Gewehr, Granate und Rakete bekämpfen, sterben und respawnen |
| M6 | Team Deathmatch | ein komplettes Match 16 gegen 16 bis 50 Punkte gespielt werden kann |
| M7 | Server-Browser & Self-Hosting | man Server im Browser findet und selbst einen hosten kann |
| M8 | Klassen & Waffen | die vier Klassen mit eigener Ausrüstung wählbar sind |
| M9 | Assets & Feinschliff | das Spiel nach einem Spiel aussieht und klingt |

## M0 – Fundament
- GitHub-Repository, `.gitignore`, `.gitattributes` (Git LFS für Unity-Binärdateien)
- .NET 10 SDK installieren, `AoS2.slnx`, `Directory.Build.props`
- `AoS2.Shared` + Testprojekt
- Leeres Unity-Projekt (URP), das `AoS2.Shared` als lokales Package einbindet
- GitHub Actions: .NET-Build und Tests bei jedem Pull Request

## M1 – Launcher & Update-Pipeline
- Launcher-Fenster (Avalonia): Status, Fortschrittsbalken, „Spielen“-Button
- Self-Update des Launchers (Velopack)
- `AoS2.ManifestBuilder`: erzeugt `manifest.json` mit Prüfsummen
- Update-Speicher einrichten (Entscheidung R2 oder MinIO)
- Nur geänderte Dateien laden, Prüfsummen kontrollieren, Spiel starten
- Platzhalter-Spiel: Unity-Build mit Versionsanzeige
- Release-Workflow: Build → Manifest → Upload

## M2 – Backend & Discord-Login
- `AoS2.Backend` (ASP.NET Core) + PostgreSQL, Deployment über Coolify
- Discord-Anwendung anlegen, OAuth2-Ablauf über das Backend
- Login im Launcher, Token sicher speichern, an das Spiel übergeben
- Signierte Join-Tickets (wird ab M4 gebraucht)

## M3 – Voxel-Welt (lokal)
- `data/blocks.json`: erste Blocktypen mit Platzhalter-Texturen
- Map-Format `.aos2map` + `AoS2.MapConverter` (MagicaVoxel-Import)
- Chunk-Rendering mit Greedy Meshing, `Texture2DArray`, Jobs + Burst
- First-Person-Bewegung im gemeinsamen Code (`AoS2.Shared`)
- Blöcke abbauen und bauen (noch ohne Server)
- Fallende Blockgruppen (Logik in `AoS2.Shared`, mit Tests)

## M4 – Netzwerk-Grundlage
- `AoS2.Server`: Verbindung über LiteNetLib, Prüfung des Join-Tickets
- Eingaben senden, Server simuliert, Snapshots zurück
- Vorhersage + Abgleich für den eigenen Spieler, Interpolation für andere
- Welt-Synchronisation beim Beitritt, Blockänderungen über den Server
- Fallende Blockgruppen übers Netz

## M5 – Kampf
- Leben, Tod, Respawn
- Hitscan-Gewehr mit Lag-Ausgleich
- Granaten und Raketen (Geschosse auf dem Server), Explosionsschaden an Blöcken und Spielern
- Treffer-Feedback, einfache Effekte

## M6 – Team Deathmatch
- Teams Blau/Grün, Teamauswahl
- Spawnzonen mit allen Regeln (kein Betreten durch Gegner, keine Blockänderung, kein Schießen hinein/heraus)
- Kills als Punkte, Siegbedingung, Matchende und Neustart
- Server-Config: Map, Punkteziel, Spieler pro Team
- Scoreboard und HUD
- **Erster richtiger Playtest mit Freunden**

## M7 – Server-Browser & Self-Hosting
- Heartbeat der Server ans Backend, Erreichbarkeitsprüfung
- Server-Browser im Spiel (Liste, Ping, Filter, Beitreten)
- Server als Windows-/Linux-Download und Docker-Image
- Offizielle Server auf Coolify
- Anleitung für Self-Hoster

## M8 – Klassen & Waffen
- `data/classes.json`, `data/weapons.json`
- Miner, Assault, Recon, Rocketeer mit eigener Ausrüstung
- Klassenauswahl-UI
- Balancing-Runden

## M9 – Assets & Feinschliff
- Texturen, Waffen- und Spielermodelle, Animationen, Sounds
- Menüs, Einstellungen (Grafik, Steuerung, Audio)
- Performance-Optimierung für 32 Spieler
- Code-Signing des Launchers

## Später
- Website
- Weitere Spielmodi
- Ingame-Map-Editor
- Statistiken und Ranglisten (nur offizielle Server)
