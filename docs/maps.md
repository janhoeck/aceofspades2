# Ace of Spades 2 – Maps bauen

Maps werden in **MagicaVoxel** gebaut und mit `tools/AoS2.MapConverter` in das Spielformat `.aos2map` umgewandelt. Das Format selbst beschreibt [architecture.md](architecture.md#5-maps).

## 1. Ablage

```
maps/
├─ testmap.aos2map            fertige Map (vom Spiel geladen)
└─ src/
   ├─ testmap.vox             MagicaVoxel-Quelle
   ├─ testmap.mapping.json    Palettenfarbe → Blocktyp
   ├─ testmap.meta.json       Name, Kampfzone, Spawnzonen, Spawnpunkte
   └─ gen-testmap.mjs         erzeugt testmap.vox (nur für die Testmap)
```

`.vox` und `.aos2map` liegen in Git LFS.

## 2. In MagicaVoxel bauen

- **Achsen:** In MagicaVoxel ist z oben. Der Konverter macht daraus die Höhe im Spiel (y).
- **1 Voxel = 1 Block.** Orientierungsgröße 512 × 512 × 64 (Breite × Tiefe × Höhe). Ein einzelnes Modell darf in MagicaVoxel höchstens 256 groß sein; größere Maps setzt man aus mehreren Modellen zusammen. Der Konverter übernimmt deren Verschiebung und Drehung aus der Szene; ausgeblendete Modelle und Ebenen lässt er weg.
- **Unterste Schicht = Boden.** Blöcke auf Höhe 0 gelten als verankert; alles, was nicht (über andere Blöcke) mit ihr verbunden ist, fällt im Spiel herunter, sobald daneben ein Block abgebaut wird. Am besten eine durchgehende Schicht Grundgestein (`bedrock`) unten.
- **Eine Farbe pro Blocktyp.** Welche Farbe welcher Blocktyp ist, legt die Zuordnungsdatei fest.

## 3. Zuordnungsdatei

```json
{
  "colors": {
    "#1c1b1e": "bedrock",
    "#726c65": "stone",
    "#586c35": "grass"
  }
}
```

Die Namen stehen in `data/blocks.json` (Feld `name`). Fehlt eine Farbe, listet der Konverter alle fehlenden Farben mit der Zahl ihrer Voxel auf, so findet man sie schnell.

## 4. Metadaten

```json
{
  "name": "Testmap",
  "author": "…",
  "combatZone": { "min": [0, 0, 24], "max": [128, 64, 104] },
  "teams": {
    "blue":  { "zone": { "min": [50, 0, 4],   "max": [78, 64, 20] },  "spawnPoints": [[60.5, 15, 12.5]] },
    "green": { "zone": { "min": [50, 0, 108], "max": [78, 64, 124] }, "spawnPoints": [[60.5, 15, 115.5]] }
  }
}
```

- Koordinaten im Spiel: `[x, Höhe, z]`, 1 = ein Block. Der Ursprung ist die kleinste belegte Ecke aller Modelle.
- Zonen sind Quader aus ganzen Blöcken: `min` gehört dazu, `max` nicht.
- Spawnpunkte sind die Füße des Spielers (Mitte unten). Auf einem Block, dessen Oberseite auf Höhe 14 liegt, ist der Spawnpunkt also `y = 15`.
- Der Konverter prüft: Zonen liegen in der Map, Spawnpunkte liegen in ihrer Spawnzone und nicht in Blöcken.

Zonen und Spawnpunkte haben in M3 noch keine Wirkung; das Spiel nutzt den ersten Spawnpunkt als Startpunkt. Die Regeln kommen in M6.

## 5. Umwandeln

```powershell
dotnet run --project tools/AoS2.MapConverter -- maps/src/testmap.vox `
  --mapping maps/src/testmap.mapping.json --meta maps/src/testmap.meta.json `
  --size 128,64,128 -o maps/testmap.aos2map
```

| Option | Bedeutung |
|---|---|
| `--mapping` | Zuordnungsdatei (Pflicht) |
| `--meta` | Metadaten; ohne heißt die Map wie die Datei und hat keine Zonen |
| `--blocks` | Blocktypen, Standard `data/blocks.json` |
| `--size x,y,z` | feste Größe (x, Höhe, z); sonst die Ausdehnung aller Voxel |
| `-o` | Ausgabedatei, Standard neben der Eingabe |

Danach im Unity-Editor auf *Play* drücken: Im Editor liest das Spiel `maps/` direkt, ein Neustart des Editors ist nicht nötig.

## 6. Testmap neu erzeugen

```powershell
node maps/src/gen-testmap.mjs
dotnet run --project tools/AoS2.MapConverter -- maps/src/testmap.vox --mapping maps/src/testmap.mapping.json --meta maps/src/testmap.meta.json --size 128,64,128 -o maps/testmap.aos2map
```

Inhalt: Grundgestein, Hügel aus Stein, Erde und Gras, zwei Spawn-Plattformen (Blau im Süden bei z ≈ 12, Grün im Norden bei z ≈ 116), eine Asphaltstraße dazwischen, eine Reihe 2 × 2-Säulen aus allen Blocktypen (z = 36), eine **T-Form** bei (40, z = 50) und eine **Brücke** zwischen zwei Baumstämmen bei z = 56 zum Testen fallender Blöcke, ein hohler Turm mit Tür bei (96, z = 80).

Wird die Testmap in MagicaVoxel von Hand weitergebaut, das Skript danach nicht mehr ausführen (es überschreibt `testmap.vox`).
