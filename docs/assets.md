# Ace of Spades 2 – Asset-Plan

> Status: Entwurf. Legt Stil, Technik, Herstellungsweg und die komplette Asset-Liste fest.

## 1. Stilrichtung

| Bereich | Entscheidung |
|---|---|
| Block-Texturen | **Pixel-Art 16 × 16** |
| Spieler & Waffen | **Blocky Low-Poly** (würfelige Modelle mit Pixel-Texturen, gebaut in Blockbench) |
| Sound | **Retro/Arcade** |
| Herstellung | **KI-gestützt**, mit manueller Nachbearbeitung |

### Grundregeln

- **Eine Pixeldichte für alles:** 16 Pixel pro Blockkante – für Blöcke, Spieler, Waffen und Effekte. Dadurch wirken alle Elemente wie aus einem Guss.
- **Maßstab:** 1 Block = 1 Unity-Einheit (= 1 Meter).
  - Spieler stehend: 1,8 Blöcke hoch (ohne Kopfbedeckung).
  - Geduckt und in Bewegung gelten die Maße, die sich aus Modell und Animation ergeben (`data/player_rig.json`, Feld `stances`): geduckt ca. 1,47 Blöcke, mit Helm ca. 1,55. Trefferzonen und Kollisionshöhe werden aus dem Modell abgeleitet, nicht von Hand festgelegt.
- **Kein Weichzeichnen:** Texturen werden ohne Filterung (Point-Filter) dargestellt, die Pixel bleiben scharf.
- **Begrenzte Farbpalette:** Eine gemeinsame Master-Palette (ca. 48–64 Farben) für alle Texturen hält den Look einheitlich. KI-Ergebnisse werden auf diese Palette reduziert.
- **Teams sofort erkennbar:**
  - Blau und Grün kräftig und klar unterschiedlich.
  - Zusätzlich ein Merkmal, das nicht nur über Farbe funktioniert (z. B. Armbinde/Helmform, Namensanzeige in Teamfarbe), damit auch farbenblinde Spieler die Teams unterscheiden.
- **Lesbarkeit vor Detail:** Gegner müssen sich klar von der Umgebung abheben. Umgebungsblöcke eher gedeckt, Spieler und Effekte eher kräftig.

## 2. KI-gestützter Arbeitsablauf

KI liefert Rohmaterial – fertig wird ein Asset erst durch die Nachbearbeitung.

### Texturen (16 × 16)

1. Mit einem Bild-Generator erzeugen, der Pixel-Art kann (z. B. Retro Diffusion, PixelLab).
2. Auf 16 × 16 herunterrechnen (Nearest Neighbor) und auf die Master-Palette reduzieren.
3. In einem Pixel-Editor säubern: Aseprite (kostenpflichtig) oder Pixelorama/LibreSprite (kostenlos).
4. **Kachelbarkeit prüfen:** Textur 3 × 3 nebeneinanderlegen – keine sichtbaren Kanten oder Muster.

### Modelle (Spieler, Waffen)

KI-3D-Generatoren erzeugen weiche Meshes, die nicht zum Block-Look passen. Deshalb:

1. KI erzeugt **Konzeptbilder** als Vorlage (Front-/Seitenansicht).
2. Modell wird in **Blockbench** aus Würfeln gebaut.
3. Textur (16 px/Block) in Blockbench malen, KI-Texturen können als Grundlage dienen.
4. Knochen (Bones) anlegen und in Blockbench animieren.
5. Export nach Unity als glTF (Import über das Unity-Paket glTFast) oder FBX.

### Sound

- **Retro-Effekte:** sfxr-artige Generatoren (jsfxr, ChipTone) – ideal für den Arcade-Stil und schnell.
- **Komplexere Effekte** (Explosionen, Graben): KI-Sound-Generatoren (z. B. ElevenLabs Sound Effects), danach mit Bitcrusher/Tiefpass in Audacity auf Retro trimmen.
- **Musik:** KI-Musikgeneratoren (z. B. Suno) im Chiptune-Stil.

### Rechte und Lizenzen

- **Vor der Nutzung eines KI-Tools prüfen**, ob dessen Bedingungen kommerzielle Nutzung erlauben (oft nur in Bezahl-Plänen).
- Rein KI-generierte Inhalte sind in vielen Ländern urheberrechtlich nur eingeschränkt geschützt. Deine eigene Nachbearbeitung stärkt das. Bei Fragen zum Verkauf: Rechtsberatung einholen.
- **Jedes Asset wird im Asset-Register erfasst** (Abschnitt 5) – Herkunft, Tool, Lizenz, Prompt.

## 3. Technische Vorgaben

### Block-Texturen

| Eigenschaft | Wert |
|---|---|
| Größe | 16 × 16 px |
| Format | PNG, RGBA, sRGB |
| Im Spiel | Filter: Point, keine Komprimierung, Mipmaps: an. Kein Unity-Import: Das Spiel baut beim Laden aus den PNGs, die `data/blocks.json` nennt, ein `Texture2DArray` (im Editor direkt aus `assets-src/textures/blocks`, im Build kopiert sie ein Build-Schritt nach `StreamingAssets`) |
| Blocktyp zuordnen | in `data/blocks.json` unter `textures` (Schlüssel `all`, `side`, `top`, `bottom`; eine Liste = Varianten) |
| Benennung | `block_<name>[_top\|_side\|_bottom][_v<n>].png` |
| Varianten | optional 2–4 Varianten pro Block gegen sichtbare Wiederholung |
| Generator | `node assets-src/tools/gen-block-textures.mjs [ordner] [--vorschau <datei.png>]` erzeugt die prozeduralen Blocktexturen (Standard: `assets-src/textures/blocks/`) – überschreibt gleichnamige PNGs, von Hand nachbearbeitete Texturen danach nicht mehr neu erzeugen |

Blöcke ohne Transparenz zuerst. Transparente Blöcke (Glas, Blätter) kommen später, weil sie das Rendering aufwendiger machen.

### Modelle

| Eigenschaft | Wert |
|---|---|
| Werkzeug | Blockbench (Quelldatei `.bbmodel`) |
| Pixeldichte | 16 px pro Block |
| Textur | ein Atlas pro Modell (64 × 64 oder 128 × 128) |
| Teamfarben | Teamfarbe über eine Masken-Textur, damit ein Modell für beide Teams reicht |
| Export | glTF oder FBX |
| Team-Texturen | `node assets-src/tools/bake-team-textures.mjs <basis.png> <maske.png>` erzeugt `_blue`/`_green` |
| Skelett, Trefferzonen, Animationen | `node assets-src/tools/export-rig.mjs <modell.bbmodel> data/<name>_rig.json` – nach **jeder** Änderung an Modell oder Animation ausführen |
| Regeln fürs Modell | keine gedrehten Würfel (Drehung nur über Gruppen/Knochen), Ausrüstung in Gruppen `gear_<klasse>_<teil>` |

### Audio

| Eigenschaft | Wert |
|---|---|
| Quellformat | WAV, 48 kHz, 16 Bit |
| Kanäle | Mono für Sounds in der Welt (3D), Stereo für UI und Musik |
| Pegel | Spitzen auf −1 dBFS normalisieren, Lautstärke-Balance im Spiel per Mixer |
| Benennung | `sfx_<kategorie>_<name>[_<n>].wav`, `music_<name>.wav` |

Von häufigen Sounds (Schuss, Schritte, Graben) 3–4 Varianten erstellen, damit es nicht monoton klingt.

## 4. Ablage im Repository

```
assets-src/                  Quelldateien (Git LFS)
├─ palette/                  Master-Palette (.gpl / .png)
├─ textures/blocks/          .aseprite / .png
├─ models/                   .bbmodel
├─ ui/
├─ audio/
├─ concepts/                 KI-Konzeptbilder, Referenzen
└─ register.csv              Asset-Register

client/Assets/Art/           Exportierte, spielfertige Assets
├─ Blocks/
├─ Models/
├─ VFX/
├─ UI/
└─ Audio/
```

Platzhalter heißen `placeholder_<name>`, damit sichtbar bleibt, was noch ersetzt werden muss.

## 5. Asset-Register

`assets-src/register.csv` – eine Zeile pro Asset:

| Spalte | Beispiel |
|---|---|
| `id` | `block_stone` |
| `datei` | `textures/blocks/block_stone.aseprite` |
| `herkunft` | `ki+eigen` / `eigen` / `cc0` / `auftrag` |
| `tool` | `Retro Diffusion, Aseprite` |
| `lizenz` | `Eigen; Tool-AGB kommerziell erlaubt (Pro-Plan)` |
| `prompt` | Prompt oder Verweis auf Datei |
| `status` | `placeholder` / `wip` / `fertig` |

## 6. Asset-Liste

Priorität: **P0** = für den Meilenstein nötig (Platzhalter reicht), **P1** = fertige Version, **P2** = später/optional.

### 6.1 Block-Texturen (ab M3)

| Block | Flächen | Eigenschaft | Prio |
|---|---|---|---|
| Gras | oben / Seite / unten (= Erde) | weich | P0 |
| Erde | alle | weich | P0 |
| Stein | alle | mittel | P0 |
| Grundgestein | alle | **unzerstörbar** (Boden der Map) | P0 |
| Spawn-Block Blau | alle | unzerstörbar | P0 |
| Spawn-Block Grün | alle | unzerstörbar | P0 |
| Baublock (von Spielern gesetzt) | alle, Teamfarbe | weich | P0 |
| Sand | alle | weich | P1 |
| Kies | alle | weich | P1 |
| Holzplanken | alle | mittel | P1 |
| Baumstamm | oben / Seite | mittel | P1 |
| Ziegel | alle | hart | P1 |
| Beton | alle | hart | P1 |
| Metallplatte | alle | sehr hart | P1 |
| Glas | alle, transparent | zerbrechlich | P2 |
| Blätter | alle, transparent | weich | P2 |

Zusätzlich:
- **Risse:** 4 Schadensstufen als Overlay-Texturen (P0 für M5).
- **Himmel:** einfacher Pixel-Himmel mit Farbverlauf und Wolken (P1).

### 6.2 Spieler (ab M4)

| Asset | Prio | Meilenstein |
|---|---|---|
| Spielermodell (Grundkörper, Teamfarben-Maske) | P0 | M4 |
| First-Person-Arme | P0 | M5 |
| Klassen-Merkmale: Helm/Ausrüstung für Miner, Assault, Recon, Rocketeer | P1 | M8 |

**Animationen** (Third-Person für andere Spieler, teilweise First-Person):

| Animation | 3rd | 1st | Prio |
|---|---|---|---|
| Stehen, Gehen, Rennen | ✔ | – | P0 |
| Ducken, Springen/Fallen | ✔ | – | P0 |
| Zielen, Schießen, Nachladen | ✔ | ✔ | P0 |
| Graben, Bauen | ✔ | ✔ | P0 |
| Werfen (Granate) | ✔ | ✔ | P1 |
| Tod (Figur zerfällt in Blöcke) | ✔ | – | P1 |
| Waffe ziehen/wechseln | – | ✔ | P1 |

### 6.3 Waffen & Werkzeuge (ab M5, endgültige Liste in M8)

| Asset | Prio |
|---|---|
| Spaten/Werkzeug zum Graben | P0 |
| Bau-Werkzeug (Block in der Hand) | P0 |
| Sturmgewehr | P0 |
| Granate | P0 |
| Raketenwerfer + Rakete | P0 |
| Scharfschützengewehr | P1 |
| Weitere Klassenwaffen (aus M8) | P1 |

### 6.4 Effekte (ab M5)

| Effekt | Prio |
|---|---|
| Block-Bruchstücke (in Farbe des Blocks) | P0 |
| Mündungsfeuer | P0 |
| Einschlag in Block | P0 |
| Explosion (Pixel-Spritesheet) | P0 |
| Rauchspur der Rakete | P1 |
| Treffer-Effekt am Spieler (Pixel-Partikel statt Blut) | P1 |
| Staub beim Aufprall fallender Blöcke | P1 |
| Sichtbare Grenze der Spawnzone | P1 |
| Leuchtspur von Geschossen | P2 |

### 6.5 Interface

| Asset | Prio | Meilenstein |
|---|---|---|
| App-Icon (Launcher + Spiel) | P0 | M1 |
| Launcher: Logo, Hintergrund, Buttons | P0 | M1 |
| Pixel-Schriftart (Lizenz OFL oder CC0) | P0 | M1 |
| Fadenkreuz | P0 | M3 |
| HUD: Leben, Munition, Blockvorrat | P0 | M5 |
| Killfeed-Icons (je Waffe) | P1 | M5 |
| Scoreboard, Punktestand | P0 | M6 |
| Teamauswahl-Bildschirm | P0 | M6 |
| Server-Browser | P0 | M7 |
| Klassen-Icons (4) | P0 | M8 |
| Hauptmenü, Einstellungen | P1 | M9 |
| Endgültiges Logo (nach Namensentscheidung) | P1 | M9 |

### 6.6 Sound (Retro/Arcade)

| Kategorie | Sounds | Prio |
|---|---|---|
| Waffen | Schuss je Waffe, Nachladen, leeres Magazin | P0 |
| Explosionen | Granate, Rakete | P0 |
| Blöcke | Graben je Material (Erde, Stein, Holz, Metall), Block setzen, Block bricht, Aufprall fallender Blöcke | P0 |
| Spieler | Schritte je Material, Springen, Landen, Treffer erhalten, Tod, Respawn | P0 |
| Feedback | Treffermarker, Kill-Bestätigung | P0 |
| Match | Start, Sieg, Niederlage | P1 |
| UI | Klick, Hover, Fehler | P1 |
| Musik | Menü-Thema, Sieg-/Niederlage-Jingle | P1 |
| Musik | Musik während des Matches | P2 |

### 6.7 Maps

| Map | Zweck | Prio | Meilenstein |
|---|---|---|---|
| Testmap: flach, klein, alle Blocktypen | Entwicklung | P0 | M3 |
| Netzwerk-Testmap: zwei kleine Spawns, kleine Kampfzone | Tests mit wenigen Spielern | P0 | M4 |
| Erste richtige Map für 16 gegen 16 | Playtest | P0 | M6 |
| Weitere Maps | Abwechslung | P2 | später |

## 7. Platzhalter-Strategie

- **M1–M2:** nur Launcher-Grafiken und App-Icon.
- **M3–M5:**
  - Einfarbige 16 × 16-Texturen mit Rauschen.
  - Spieler als Würfel-Figur, Waffen als einfache Quader.
  - Sounds per jsfxr.
- **Ab M6:** Platzhalter schrittweise durch P1-Assets ersetzen, beginnend mit allem, was das Spielgefühl am stärksten prägt: Waffensounds, Explosion, Blocktexturen, Spielermodell.

So blockiert die Asset-Produktion nie die Programmierung, und beides kann parallel laufen.

## 8. Offen

- Sollen Spieler neben den Namen über dem Kopf weitere Team-Merkmale haben (z. B. Helmform)?
- Musik während des Matches: ja oder nein?
- Endgültiges Logo erst nach der Namensentscheidung.
