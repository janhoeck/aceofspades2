# Ace of Spades 2 – Game Design

> Arbeitstitel, eventuell spätere Umbenennung.
> Status: Entwurf – wird laufend gemeinsam erweitert.

## 1. Kurzbeschreibung

Ein Team-Shooter in einer vollständig zerstörbaren Block-Welt. Zwei Teams (**Blau** und **Grün**) bekämpfen sich mit Waffen, Granaten und Raketen. Jeder Block der Kampfzone kann abgebaut, zerschossen, gesprengt oder neu gebaut werden.

## 2. Spielmodus

### Team Deathmatch (einziger Modus zum Start)

| Regel | Wert (Standard) | Anpassbar |
|---|---|---|
| Punkt für das Team | 1 pro Kill eines Gegners | – |
| Siegbedingung | erstes Team mit **50** Punkten | ja (Server-Config) |
| Spieler pro Team | **16** (16 gegen 16) | ja (Server-Config) |

Noch offen:
- Gibt es ein Zeitlimit? Was passiert bei Gleichstand?
- Teambeschuss (Friendly Fire): aus oder an?
- Selbstmord / Fallschaden / von eigenen Blöcken erschlagen: Punktabzug oder nichts?
- Respawn-Zeit (Vorschlag: 5 Sekunden)

## 3. Klassen

Die Klasse wird beim Spielbeginn gewählt (und beim Respawn wechselbar – noch offen).

| Klasse | Rolle (Idee) |
|---|---|
| **Miner** | Graben, Tunnel, schnelles Abbauen und Bauen |
| **Assault** | Allrounder, Sturmgewehr |
| **Recon** | Distanzkampf, Mobilität |
| **Rocketeer** | Zerstörung auf Distanz, Raketenwerfer |

Waffen, Werte und Ausrüstung werden später festgelegt. Alle Klassen und Waffen sind **datengetrieben** (JSON-Dateien in `data/`), damit Balancing ohne Code-Änderung möglich ist.

### Trefferzonen

Getroffen wird genau das, was man sieht: Jeder Würfel des Spielermodells ist eine eigene Trefferbox und bewegt sich mit der Animation mit – auch beim Ducken, Rennen und Springen. Die Ausrüstung der gewählten Klasse (Helm, Weste, Rucksack …) gehört dazu. Jede Box gehört zu einer Zone: **Kopf**, **Rumpf**, **Arm** oder **Bein**. Technik: siehe `docs/architecture.md`, Abschnitt Trefferzonen.

Noch offen:
- Schadensfaktor je Zone (z. B. Kopf ×2, Beine ×0,75)?
- Zählen dünne Ausrüstungsteile (z. B. die Funkantenne des Recon) als Treffer?
- Hat jede Klasse ein Werkzeug zum Abbauen (z. B. Spaten) und Blöcke zum Bauen?
- Begrenzter Block-Vorrat pro Spieler?

## 4. Welt & Blöcke

- Blockartige Grafik mit **texturierten Blöcken** (Blocktypen wie Erde, Stein, Holz, Beton …).
- Jeder Blocktyp hat eigene Eigenschaften: Textur, Lebenspunkte, Sound, ggf. Zerstörbarkeit.
- Blöcke können **abgebaut** (Werkzeug), **beschädigt** (Kugeln) und **gesprengt** (Granaten, Raketen) werden.
- Spieler können **Blöcke bauen**.
- **Schwebende Blöcke fallen herunter:** Verliert eine Blockgruppe die Verbindung zum Boden, fällt sie als Ganzes nach unten und wird beim Aufprall wieder Teil der Welt.
  - Noch offen: Gehen beim Aufprall Blöcke kaputt? Erhalten getroffene Spieler Schaden?

## 5. Maps & Zonen

- Maps werden **vorher gebaut**, nicht zufällig generiert.
- Jede Map hat:
  - eine **Kampfzone**: alles darin ist zerstörbar und bebaubar.
  - zwei **Spawnzonen** (eine pro Team) außerhalb der Kampfzone.
- Regeln für Spawnzonen:
  - Gegner können die Spawnzone nicht betreten (unsichtbare Wand).
  - Blöcke in der Spawnzone können nicht zerstört oder gebaut werden – auch nicht durch Explosionen.
  - Man kann **nicht aus der Spawnzone heraus und nicht in sie hinein schießen**. Geschosse werden an der Zonengrenze gestoppt, Waffen sind im Spawn nicht benutzbar.
- Orientierungsgröße: ca. 512 × 512 × 64 Blöcke (wie das Original) – je nach Performance anpassbar.

## 6. Spieler & Accounts

- Anmeldung über **Discord-Login** im Launcher.
- Der Discord-Name dient als Standard-Spielername (ggf. änderbar – offen).

## 7. Server

- Es gibt einen **Server-Browser**, der alle laufenden Server anzeigt.
- **Jeder kann einen eigenen Server hosten.** Zusätzlich gibt es offizielle Server.
- Server-Einstellungen (Map, Punkteziel, Spieleranzahl, …) legt der Betreiber per Config fest.

## 8. Plattform

- Zum Start nur **Windows**.
