# Balancing-Tabelle

> Erzeugt aus `data/*.json` mit `dotnet run --project tools/AoS2.Balance -- --out docs/balancing.md`. Nicht von Hand bearbeiten:
> Werte in `data/weapons.json`, `data/classes.json` und `data/combat.json` ändern und neu erzeugen. Anleitung: [game-design.md](game-design.md#balancing).

Leben 100 (wenn die Klasse nichts anderes sagt), Zonenfaktoren Kopf ×2, Rumpf ×1, Arm ×0,67, Bein ×0,67, Waffenwechsel 0,3 s, Respawn 5 s.

## Klassen

| Klasse | Platz 2 … 5 | Blöcke | Pause Abbauen/Bauen | Gehen / Sprinten | Höhe stehend / geduckt | Leben |
|---|---|---|---|---|---|---|
| Miner | MP, Granate | 100 | 0,1 s | 4,5 / 6,5 Blöcke/s | 1,97 / 1,54 | 100 |
| Assault (Standard) | Sturmgewehr, Pistole, Granate | 50 | 0,2 s | 4,5 / 6,5 Blöcke/s | 1,97 / 1,54 | 100 |
| Recon | Scharfschützengewehr, Pistole | 30 | 0,2 s | 4,95 / 7,15 Blöcke/s | 1,97 / 1,54 | 100 |
| Rocketeer | Raketenwerfer, Pistole | 30 | 0,2 s | 4,05 / 5,85 Blöcke/s | 1,98 / 1,56 | 100 |

## Gewehre

| Waffe | Klassen | Schaden | Schuss/s | Schaden/s | Magazin + Reserve | Nachladen | Abfall | Reichweite |
|---|---|---|---|---|---|---|---|---|
| Sturmgewehr | Assault | 25 | 10 | 250 | 30 + 120 | 2 s | ab 40, 60 % ab 100 | 200 |
| MP | Miner | 20 | 15 | 300 | 32 + 160 | 1,8 s | ab 20, 50 % ab 60 | 150 |
| Scharfschützengewehr | Recon | 70 | 0,83 (Einzelfeuer) | 58 | 5 + 20 | 2,5 s | keiner | 300 |
| Pistole | Assault, Recon, Rocketeer | 30 | 5 (Einzelfeuer) | 150 | 8 + 32 | 1,5 s | ab 25, 50 % ab 60 | 120 |

## Treffer bis zum Tod

Je Zone und Entfernung (Blöcke): Treffer bis zum Tod und in Klammern die Zeit vom ersten bis zum tödlichen Treffer, wenn jeder
Schuss trifft und so schnell wie möglich geschossen wird (mit Nachladen, falls das Magazin nicht reicht). – = außer Reichweite.

### Sturmgewehr

| Zone | 0 | 25 | 50 | 100 | 200 |
|---|---|---|---|---|---|
| Kopf | 2 (0,10 s) | 2 (0,10 s) | 3 (0,20 s) | 4 (0,30 s) | 4 (0,30 s) |
| Rumpf | 4 (0,30 s) | 4 (0,30 s) | 5 (0,40 s) | 7 (0,60 s) | 7 (0,60 s) |
| Arm | 6 (0,50 s) | 6 (0,50 s) | 7 (0,60 s) | 10 (0,90 s) | 10 (0,90 s) |
| Bein | 6 (0,50 s) | 6 (0,50 s) | 7 (0,60 s) | 10 (0,90 s) | 10 (0,90 s) |

### MP

| Zone | 0 | 25 | 50 | 100 | 200 |
|---|---|---|---|---|---|
| Kopf | 3 (0,13 s) | 3 (0,13 s) | 4 (0,20 s) | 5 (0,27 s) | – |
| Rumpf | 5 (0,27 s) | 6 (0,33 s) | 8 (0,47 s) | 10 (0,60 s) | – |
| Arm | 8 (0,47 s) | 8 (0,47 s) | 12 (0,73 s) | 15 (0,93 s) | – |
| Bein | 8 (0,47 s) | 8 (0,47 s) | 12 (0,73 s) | 15 (0,93 s) | – |

### Scharfschützengewehr

| Zone | 0 | 25 | 50 | 100 | 200 |
|---|---|---|---|---|---|
| Kopf | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) |
| Rumpf | 2 (1,20 s) | 2 (1,20 s) | 2 (1,20 s) | 2 (1,20 s) | 2 (1,20 s) |
| Arm | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) |
| Bein | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) | 3 (2,40 s) |

### Pistole

| Zone | 0 | 25 | 50 | 100 | 200 |
|---|---|---|---|---|---|
| Kopf | 2 (0,20 s) | 2 (0,20 s) | 3 (0,40 s) | 4 (0,60 s) | – |
| Rumpf | 4 (0,60 s) | 4 (0,60 s) | 5 (0,80 s) | 7 (1,20 s) | – |
| Arm | 5 (0,80 s) | 5 (0,80 s) | 8 (1,40 s) | 10 (3,12 s) | – |
| Bein | 5 (0,80 s) | 5 (0,80 s) | 8 (1,40 s) | 10 (3,12 s) | – |

## Sprengstoff

Schaden an Spielern nach Abstand vom Mittelpunkt (ohne Zonen, Deckung hinter Blöcken schützt ganz).

| Waffe | Klassen | Anzahl | Mitte | 1 Block | 2 Blöcke | 3 Blöcke | tödlich bis | Radius | Blöcke: Radius / Schaden Mitte |
|---|---|---|---|---|---|---|---|---|---|
| Granate | Miner, Assault | 3 + 0 | 120 | 93 | 67 | 40 | 0,75 Blöcke | 4,5 | 2,5 / 220 |
| Raketenwerfer | Rocketeer | 1 + 5 | 120 | 90 | 60 | 30 | 0,67 Blöcke | 4 | 3 / 260 |

