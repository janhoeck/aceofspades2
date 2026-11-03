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
| Selbstmord, Teamkill | kein Punkt, kein Abzug (der Tod zählt in der Statistik) | – |
| Siegbedingung | erstes Team mit **50** Punkten | ja (`scoreLimit` in der Server-Config) |
| Zeitlimit | **keins**; wenn gesetzt, gewinnt bei Ablauf das führende Team, bei Gleichstand ist es unentschieden | ja (`timeLimitMinutes`) |
| Spieler pro Team | **16** (16 gegen 16) | ja (`playersPerTeam`, höchstens 24) |
| Teambeschuss (Friendly Fire) | **aus**: Schüsse und Raketen gehen durch Mitspieler, Explosionen schaden ihnen nicht | ja (`friendlyFire`) |
| Matchende | **15 Sekunden** Ergebnis-Anzeige, niemand kann kämpfen, bauen oder laufen; dann Neustart | ja (`matchEndSeconds`) |
| Neustart | Map wie neu, Punkte, Kills und Tode auf 0, alle spawnen neu, **die Teams bleiben** | – |
| Leben | 100 | ja (`data/combat.json`) |
| Respawn-Zeit | **5 Sekunden**, dann an einem Spawnpunkt des eigenen Teams mit vollem Leben und voller Munition | ja (`data/combat.json`) |
| Eigene Explosionen | schaden **voll** (Faktor 1), auch ohne Teambeschuss | ja (`selfDamage` in `data/combat.json`) |

### Teams (seit M6)

- Wer einem Server beitritt, ist zuerst **Zuschauer** und wählt in einem Menü **Blau**, **Grün** oder **Automatisch** (das kleinere Team; bei Gleichstand das mit weniger Punkten, sonst Blau). Erst dann erscheint man im Spawn des Teams.
- **Ausgleich:** Ein Team darf höchstens **einen Spieler mehr** haben als das andere, und nie mehr als `playersPerTeam`. Gesperrte Teams sind im Menü ausgegraut; der Server lehnt sie ohnehin ab.
- **Teamwechsel** (Taste **M**, dasselbe Menü): Man stirbt sofort (zählt nicht als Tod, gibt niemandem einen Punkt) und erscheint nach der normalen Respawn-Zeit im neuen Team.
- Verlässt jemand den Server, werden die Teams nicht automatisch neu verteilt (die Spieler wählen selbst; das Menü zeigt, wo Platz ist).
- Die Spielerfarbe zeigt das Team (Texturen aus `assets-src/models/player/`, eingefärbt mit `bake-team-textures.mjs`), Namen über den Köpfen und in der Kill-Anzeige ebenso.

### Anzeige im Spiel (seit M6, Platzhalter bis M9)

- Oben in der Mitte: Punkte beider Teams, Punkteziel, Restzeit (mit Zeitlimit).
- **Tab** gedrückt: Punktetabelle, beide Teams nebeneinander, Spieler nach Kills sortiert, mit Toden.
- Matchende: Sieger (oder „Unentschieden“), Endstand, Countdown bis zum nächsten Match und die Punktetabelle.
- In der eigenen Spawnzone ein Hinweis, dass hier niemand schießen kann.

Noch offen:
- Fallschaden / von eigenen Blöcken erschlagen: Punktabzug oder nichts? (Fallschaden gibt es noch nicht.)
- Automatischer Teamausgleich, wenn Spieler gehen? (Zum Start nicht: siehe oben.)

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

Schadensfaktor je Zone (seit M5, `data/combat.json`): **Kopf ×2, Rumpf ×1, Arme ×0,67, Beine ×0,67**, wie im Original: Mit dem Gewehr (25 Schaden) braucht man 4 Rumpftreffer, 2 Kopfschüsse oder 6 Treffer an Armen und Beinen. Die Waffe in der Hand ist keine Trefferzone.

Noch offen:
- Zählen dünne Ausrüstungsteile (z. B. die Funkantenne des Recon) als Treffer?
- Hat jede Klasse ein Werkzeug zum Abbauen (z. B. Spaten) und Blöcke zum Bauen?
- Begrenzter Block-Vorrat pro Spieler?

### Waffen (Stand M5, für alle Spieler gleich)

Bis die Klassen kommen (M8), hat jeder dieselben Waffen. Alle Werte stehen in `data/weapons.json`.

| Platz | Waffe | Werte |
|---|---|---|
| 1 | Spaten | abbauen und bauen |
| 2 | Sturmgewehr | Hitscan, 25 Schaden, 10 Schuss/s Dauerfeuer, 30 Schuss im Magazin, 120 Reserve, 2 s Nachladen, 15 Schaden an Blöcken |
| 3 | Granate | 3 Stück, geworfen, prallt ab, explodiert nach 3 s; bis 120 Schaden im Umkreis von 4,5 Blöcken, sprengt Blöcke im Umkreis von 2,5 |
| 4 | Raketenwerfer | 1 Rakete im Rohr, 5 Reserve, 1,5 s Nachladen; fliegt gerade, explodiert beim Aufprall: bis 120 Schaden im Umkreis von 4 Blöcken, sprengt Blöcke im Umkreis von 3 |

Explosionsschaden nimmt zum Rand hin ab, Deckung hinter Blöcken schützt. Nach einem Waffenwechsel ist die Waffe 0,3 s nicht bereit. Leere Magazine laden von selbst nach.

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
- Umsetzung (seit M6), Festlegungen dazu:
  - Die unsichtbare Wand gilt für Gegner auch von oben: Eine Spawnzone ist für sie ein fester Quader, auf dem man stehen kann. Deshalb reichen Spawnzonen bis zum Himmel (Höhe der Map).
  - Gewehrschüsse enden an der Zonengrenze. Raketen explodieren davor, Granaten prallen ab. Wer in der Spawnzone steht, bekommt keinen Schaden, auch nicht von Explosionen an der Grenze.
  - Der Spaten ist keine Waffe: Aus dem Spawn heraus darf man Blöcke außerhalb abbauen und bauen (sofern sie in der Kampfzone liegen).
  - **Nur in der Kampfzone** sind Blöcke veränderbar. Was außerhalb liegt (Spawnzonen, der Weg vom Spawn zur Kampfzone, der Rand), bleibt unverändert, und daran hängende Gruppen fallen nicht.
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
