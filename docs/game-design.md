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
| Respawn-Zeit | **5 Sekunden**, dann an einem Spawnpunkt des eigenen Teams mit vollem Leben, voller Munition und vollem Blockvorrat (und der gewählten Klasse) | ja (`data/combat.json`) |
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

Vier Klassen mit eigener Ausrüstung (seit M8). Alle Klassen und Waffen sind **datengetrieben**: Klassen in `data/classes.json`, Waffen in `data/weapons.json`, Kampfregeln in `data/combat.json`. So geht Balancing ohne Code-Änderung (Abschnitt [Balancing](#balancing)).

| Klasse | Rolle | Platz 2 | Platz 3 | Platz 4 | Blöcke | Tempo | Ausrüstung am Modell |
|---|---|---|---|---|---|---|---|
| **Miner** | Graben, Tunnel, schnelles Abbauen und Bauen | MP | Granate ×3 | – | 100 | normal | Helm mit Lampe, Werkzeuggürtel mit Taschen |
| **Assault** | Allrounder (Standardklasse) | Sturmgewehr | Pistole | Granate ×3 | 50 | normal | Helm, Weste mit Magazinen |
| **Recon** | Distanzkampf, Mobilität | Scharfschützengewehr mit Zielfernrohr | Pistole | – | 30 | +10 % | Hut, Entfernungsmesser, Schal, Funkgerät mit Antenne |
| **Rocketeer** | Zerstörung auf Distanz | Raketenwerfer | Pistole | – | 30 | −10 % | Raketenhelm mit Visier, Schulterpolster, Rucksack mit Raketen |

Platz 1 ist bei allen der Spaten. Alle Klassen haben 100 Leben (ein eigener Wert je Klasse ginge mit `health` in classes.json).

### Klassenwahl (Entscheidungen M8)

- **Wahl:** Nach dem Beitritt zeigt das Menü oben die Klassen als Karten (Waffen, Blöcke, Tempo, Rumpftreffer bis zum Tod), unten die Teams. Man wählt zuerst die Klasse und erscheint mit ihr, sobald man ein Team wählt. Wer keine wählt, spielt die Standardklasse **Assault** (`defaultClass`).
- **Wechsel beim nächsten Respawn:** Mit **K** (nur Klassen) oder **M** (Klasse und Team) kann man jederzeit eine andere Klasse wählen. Sie gilt ab dem nächsten Spawn: Wer lebt, behält Klasse, Waffen und Munition bis zum nächsten Tod oder Teamwechsel; wer tot ist, erscheint beim Respawn mit der neuen. Ein sofortiger Wechsel hätte Leben und Munition aufgefüllt. Das HUD zeigt „Nächster Spawn: …“.
- **Werkzeug für alle:** Jede Klasse hat den Spaten und Baublöcke. Der **Miner** gräbt und baut doppelt so schnell (0,1 s Pause statt 0,2 s, `editSeconds`) und trägt die meisten Blöcke.
- **Begrenzter Blockvorrat:** Jede Klasse trägt höchstens so viele Blöcke wie `blocks` in classes.json. Bauen kostet einen Block, Abbauen mit dem Spaten gibt einen zurück (nie über das Maximum), der Respawn füllt auf. Zerschossene oder gesprengte Blöcke geben nichts zurück. Der Server prüft den Vorrat; das HUD zeigt ihn beim Spaten.
- **Tempo:** Der Recon läuft, sprintet und schleicht 10 % schneller, der Rocketeer mit dem schweren Rucksack 10 % langsamer (`speed`).

### Trefferzonen

Getroffen wird genau das, was man sieht: Jeder Würfel des Spielermodells ist eine eigene Trefferbox und bewegt sich mit der Animation mit – auch beim Ducken, Rennen und Springen. Die Ausrüstung der gewählten Klasse (Helm, Weste, Rucksack …) gehört dazu. Jede Box gehört zu einer Zone: **Kopf**, **Rumpf**, **Arm** oder **Bein**. Technik: siehe `docs/architecture.md`, Abschnitt Trefferzonen.

Schadensfaktor je Zone (seit M5, `data/combat.json`): **Kopf ×2, Rumpf ×1, Arme ×0,67, Beine ×0,67**, wie im Original: Mit dem Gewehr (25 Schaden) braucht man 4 Rumpftreffer, 2 Kopfschüsse oder 6 Treffer an Armen und Beinen. Die Waffe in der Hand ist keine Trefferzone.

**Ausrüstung (Entscheidung M8):** Sichtbares Modell und Server-Hitboxen enthalten genau die Ausrüstung der Klasse. Jedes Teil zählt mit der Zone seines Knochens: Helme und Hüte sind Kopf, Weste, Gürtel, Funkgerät und Rucksack Rumpf, die Schulterpolster Arm. Wer Ausrüstung trägt, ist also etwas größer (der Rocketeer mit Rucksack am meisten). **Ausnahme:** Teile, die in einer Richtung dünner als 1 Pixel sind (die Antenne und der Knopf am Funkgerät des Recon, 2,5 cm), sind nur Optik (`minHitboxPixels` in combat.json). Sonst gäbe es Treffer, die sich zufällig anfühlen. Die Kollisionshöhe beim Laufen kommt ebenfalls aus dem Modell mit Ausrüstung (stehend 1,97 Blöcke mit Helm, 1,98 mit Raketenhelm, 1,90 ohne).

### Waffen (Stand M8)

Alle Werte stehen in `data/weapons.json`, die Zuordnung zu den Klassen in `data/classes.json`. Treffer und Zeit bis zum Tod je Zone und Entfernung: [balancing.md](balancing.md).

| Waffe | Klassen | Werte |
|---|---|---|
| Spaten | alle | abbauen und bauen |
| Sturmgewehr | Assault | Hitscan, 25 Schaden, 10 Schuss/s Dauerfeuer, 30 + 120 Schuss, 2 s Nachladen; voller Schaden bis 40 Blöcke, dann weniger bis 60 % ab 100 |
| MP | Miner | Hitscan, 20 Schaden, 15 Schuss/s Dauerfeuer, 32 + 160 Schuss, 1,8 s Nachladen; voller Schaden bis 20 Blöcke, dann weniger bis 50 % ab 60 |
| Scharfschützengewehr | Recon | Hitscan, 70 Schaden, Einzelfeuer alle 1,2 s, 5 + 20 Schuss, 2,5 s Nachladen, kein Abfall, Zielfernrohr 4-fach (rechte Maustaste) |
| Pistole | Assault, Recon, Rocketeer | Hitscan, 30 Schaden, Einzelfeuer bis 5 Schuss/s, 8 + 32 Schuss, 1,5 s Nachladen; voller Schaden bis 25 Blöcke, dann weniger bis 50 % ab 60 |
| Granate | Miner, Assault | 3 Stück, geworfen, prallt ab, explodiert nach 3 s; bis 120 Schaden im Umkreis von 4,5 Blöcken, sprengt Blöcke im Umkreis von 2,5 |
| Raketenwerfer | Rocketeer | 1 Rakete im Rohr, 5 Reserve, 1,5 s Nachladen; fliegt gerade, explodiert beim Aufprall: bis 120 Schaden im Umkreis von 4 Blöcken, sprengt Blöcke im Umkreis von 3 |

Explosionsschaden nimmt zum Rand hin ab, Deckung hinter Blöcken schützt. Nach einem Waffenwechsel ist die Waffe 0,3 s nicht bereit. Leere Magazine laden von selbst nach. Waffen ohne Dauerfeuer brauchen für jeden Schuss einen neuen Klick.

### Balancing

**Erste Werte (M8) und warum:**

- **Sturmgewehr bleibt wie in M5** (4 Rumpftreffer, 0,3 s): Es ist der Maßstab, an dem sich die anderen messen.
- **MP** (Miner): auf kurze Distanz etwas schneller tödlich als das Gewehr (5 Rumpftreffer in 0,27 s), auf mittlere und große Distanz klar schlechter (Abfall ab 20 Blöcken). Der Miner kämpft in seinen Tunneln. Abweichung vom ersten Vorschlag (18 Schaden, 13 Schuss/s): 13 Schuss/s geht bei 60 Ticks/s nicht (12 oder 15), und mit 18 Schaden wäre die MP auch aus der Nähe langsamer als das Gewehr (6 statt 5 Treffer). Deshalb 20 Schaden bei 15 Schuss/s.
- **Scharfschützengewehr** (Recon): 70 Schaden, also tötet ein Kopfschuss auf jede Entfernung, zwei Rumpftreffer auch (1,2 s). Dafür Einzelfeuer, 5 Schuss im Magazin, 30 Blöcke Vorrat. Kein Abfall: Es ist die einzige Waffe, die auf 100 Blöcke und mehr voll trifft.
- **Pistole** (Zweitwaffe): 4 Rumpftreffer in 0,6 s aus der Nähe. Langsamer als die Hauptwaffen, aber eine echte Rettung für Recon und Rocketeer auf kurze Distanz.
- **Schadensabfall** (`falloff`): Ohne Streuung wäre sonst die Waffe mit dem meisten Schaden pro Sekunde überall die beste. Mit Abfall hat jede Waffe ihre Entfernung.
- **Raketenwerfer, Granaten** wie in M5. Tödlich nur bei einem fast direkten Treffer (bis 0,67 bzw. 0,75 Blöcke vom Mittelpunkt), danach Schaden bis 4 Blöcke: Sie zerstören vor allem Deckung.
- **Blöcke:** Miner 100, Assault 50 (wie im Original), Recon und Rocketeer 30. Abbauen füllt auf, Bauen braucht also Graben.
- **Tempo:** ±10 % ist spürbar, ohne dass man den Recon nicht mehr treffen kann (4,95 statt 4,5 Blöcke/s beim Gehen).

**Nachjustieren beim Playtest, Schritt für Schritt:**

1. Wert ändern: Waffen in `data/weapons.json` (Schaden, `fireInterval`, Magazin, `falloff`, Explosion), Klassen in `data/classes.json` (Waffen, `blocks`, `editSeconds`, `speed`, `health`), Zonenfaktoren und Leben in `data/combat.json`.
2. Tabelle neu rechnen: `dotnet run --project tools/AoS2.Balance -- --out docs/balancing.md`. Sie zeigt für jede Waffe, wie viele Treffer an Kopf, Rumpf, Arm und Bein auf 0, 25, 50, 100 und 200 Blöcke töten und wie lange das mindestens dauert (alle Schüsse treffen, so schnell wie möglich, mit Nachladen). Dazu Klassen und Sprengstoff. Ungültige Daten lehnt das Werkzeug mit derselben Meldung ab wie der Server.
3. Server neu starten. Die Spiele bekommen die Werte des Servers beim Beitritt; ein Spiel-Update ist dafür nicht nötig.

Faustregeln: Die Zeit bis zum Tod (Rumpf, auf der typischen Entfernung der Klasse) sollte für die Hauptwaffen mit Dauerfeuer nah beieinander liegen (0,25 … 0,5 s), sonst gibt es eine beste Klasse. Das Scharfschützengewehr gleicht seine langsame Feuerrate mit dem Kopfschuss und der Reichweite aus. Ein Wert ändert sich am besten in kleinen Schritten (±10 %), und immer nur einer auf einmal.

Noch offen:
- Munition auffüllen ohne Tod (Kisten, Spawn)? Zum Start nur durch den Respawn.
- Eigene Klassen-Icons (Platzhalter: Anfangsbuchstabe auf Klassenfarbe) und die Waffenmodelle aus Blockbench kommen in M9.

## 4. Welt & Blöcke

- Blockartige Grafik mit **texturierten Blöcken** (Blocktypen wie Erde, Stein, Holz, Beton …).
- Jeder Blocktyp hat eigene Eigenschaften: Textur, Lebenspunkte, Sound, ggf. Zerstörbarkeit.
- Blöcke können **abgebaut** (Werkzeug), **beschädigt** (Kugeln) und **gesprengt** (Granaten, Raketen) werden.
- Spieler können **Blöcke bauen**, aus einem Vorrat je Klasse (Abbauen füllt ihn wieder auf, siehe Abschnitt 3).
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
