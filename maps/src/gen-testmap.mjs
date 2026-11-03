// Erzeugt die Entwicklungs-Testmap als MagicaVoxel-Datei (testmap.vox, 128 × 128 × 64).
// Danach mit dem Konverter in eine .aos2map umwandeln (siehe docs/maps.md).
//
// Aufruf:  node maps/src/gen-testmap.mjs [ausgabe.vox]
//
// Inhalt: Grundgestein, sanfte Hügel (Stein, Erde, Gras), zwei Spawn-Plattformen (Blau im Süden,
// Grün im Norden), eine Asphaltstraße dazwischen, eine Reihe Säulen aus allen Blocktypen, eine
// T-Form und eine Brücke zum Testen fallender Blöcke, ein hohler Turm.
// Achsen wie in MagicaVoxel: x, y = Boden, z = oben. Im Spiel wird z zur Höhe (y).
//
// Die Farben müssen zu maps/src/testmap.mapping.json passen.

import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SX = 128, SY = 128, SZ = 64;

// Farbindex -> [Farbe, Blocktyp]
const PALETTE = [
  null,
  ['1c1b1e', 'bedrock'],
  ['726c65', 'stone'],
  ['5a4430', 'dirt'],
  ['586c35', 'grass'],
  ['353538', 'asphalt'],
  ['524030', 'log'],
  ['2853bd', 'spawn_blue'],
  ['1f9a3d', 'spawn_green'],
  ['5a88ea', 'build_blue'],
  ['58d074', 'build_green'],
];
const C = Object.fromEntries(PALETTE.slice(1).map(([, name], i) => [name, i + 1]));

const vox = new Uint8Array(SX * SY * SZ); // Index (z * SY + y) * SX + x
const set = (x, y, z, c) => {
  if (x >= 0 && y >= 0 && z >= 0 && x < SX && y < SY && z < SZ) vox[(z * SY + y) * SX + x] = c;
};
const get = (x, y, z) => vox[(z * SY + y) * SX + x];

// ---- Gelände ----

const SPAWN_HEIGHT = 14;
const spawnRect = { x0: 50, x1: 78 }; // x-Bereich beider Spawn-Plattformen
const blueY = [4, 20], greenY = [108, 124]; // y-Bereich (Boden-Achse) der Plattformen
const inSpawn = (x, y) => x >= spawnRect.x0 && x < spawnRect.x1 && ((y >= blueY[0] && y < blueY[1]) || (y >= greenY[0] && y < greenY[1]));

function terrainHeight(x, y) {
  const h = 12 + 3 * Math.sin(x / 11) * Math.cos(y / 13) + 2 * Math.sin((x + y) / 17);
  // Zu den Spawn-Plattformen hin sanft auf deren Höhe bringen
  const dx = Math.max(spawnRect.x0 - x, 0, x - spawnRect.x1 + 1);
  const dy = Math.min(Math.max(blueY[0] - y, 0, y - blueY[1] + 1), Math.max(greenY[0] - y, 0, y - greenY[1] + 1));
  const d = Math.hypot(dx, dy);
  const t = Math.min(d / 10, 1);
  return Math.round(SPAWN_HEIGHT * (1 - t) + h * t);
}

const height = [];
for (let y = 0; y < SY; y++) {
  for (let x = 0; x < SX; x++) {
    const h = terrainHeight(x, y);
    height[y * SX + x] = h;
    set(x, y, 0, C.bedrock);
    for (let z = 1; z <= h; z++) set(x, y, z, z <= h - 4 ? C.stone : z < h ? C.dirt : C.grass);
  }
}
const top = (x, y) => height[y * SX + x];

// ---- Spawn-Plattformen ----

for (let y = 0; y < SY; y++) {
  for (let x = 0; x < SX; x++) {
    if (!inSpawn(x, y)) continue;
    const team = y < 64 ? C.spawn_blue : C.spawn_green;
    for (let z = 1; z <= SPAWN_HEIGHT; z++) set(x, y, z, z === SPAWN_HEIGHT ? team : C.stone);
    for (let z = SPAWN_HEIGHT + 1; z < SZ; z++) set(x, y, z, 0);
    height[y * SX + x] = SPAWN_HEIGHT;
  }
}

// ---- Straße von Spawn zu Spawn ----

for (let y = blueY[1]; y < greenY[0]; y++) {
  for (let x = 62; x < 66; x++) set(x, y, top(x, y), C.asphalt);
}

// ---- Säulen aus allen Blocktypen (2 × 2, 4 hoch) ----

PALETTE.slice(1).forEach(([, name], i) => {
  const x0 = 14 + i * 5, y0 = 36;
  let base = 0;
  for (let y = y0; y < y0 + 2; y++) for (let x = x0; x < x0 + 2; x++) base = Math.max(base, top(x, y));
  for (let y = y0; y < y0 + 2; y++) {
    for (let x = x0; x < x0 + 2; x++) {
      for (let z = top(x, y) + 1; z <= base; z++) set(x, y, z, C.stone); // Sockel auf Hanglage
      for (let z = base + 1; z <= base + 4; z++) set(x, y, z, C[name]);
    }
  }
});

// ---- T-Form: Säule mit Plattform; Säule abbauen = Plattform fällt ----

{
  const px = 40, py = 50, base = top(px, py);
  for (let z = base + 1; z <= base + 6; z++) set(px, py, z, C.stone);
  for (let y = py - 3; y <= py + 3; y++) for (let x = px - 3; x <= px + 3; x++) set(x, y, base + 7, C.build_blue);
}

// ---- Brücke zwischen zwei Stämmen; erst beide Stämme durchtrennen, dann fällt sie ----

{
  const y0 = 56, xa = 84, xb = 96;
  const deck = Math.max(top(xa, y0), top(xb, y0)) + 7;
  for (const x of [xa, xb]) for (let z = top(x, y0) + 1; z < deck; z++) set(x, y0, z, C.log);
  for (let x = xa; x <= xb; x++) for (let y = y0 - 1; y <= y0 + 1; y++) set(x, y, deck, C.build_green);
}

// ---- Hohler Turm mit Tür ----

{
  const x0 = 96, y0 = 80, size = 6, h = 12;
  let base = 0;
  for (let y = y0; y < y0 + size; y++) for (let x = x0; x < x0 + size; x++) base = Math.max(base, top(x, y));
  for (let y = y0; y < y0 + size; y++) {
    for (let x = x0; x < x0 + size; x++) {
      const wall = x === x0 || y === y0 || x === x0 + size - 1 || y === y0 + size - 1;
      for (let z = top(x, y) + 1; z <= base; z++) set(x, y, z, C.stone);
      for (let z = base + 1; z <= base + h; z++) set(x, y, z, wall || z === base + h ? C.stone : 0);
    }
  }
  // Tür (2 hoch) in der Südwand
  set(x0 + 2, y0, base + 1, 0);
  set(x0 + 2, y0, base + 2, 0);
}

// ---- .vox schreiben (Version 150: SIZE, XYZI, RGBA; MagicaVoxel ergänzt den Rest beim Speichern) ----

function chunk(id, content) {
  const head = Buffer.alloc(12);
  head.write(id, 0, 'ascii');
  head.writeInt32LE(content.length, 4);
  head.writeInt32LE(0, 8);
  return Buffer.concat([head, content]);
}

const voxels = [];
for (let z = 0; z < SZ; z++) for (let y = 0; y < SY; y++) for (let x = 0; x < SX; x++) {
  const c = get(x, y, z);
  if (c) voxels.push(x, y, z, c);
}
const size = Buffer.alloc(12);
size.writeInt32LE(SX, 0);
size.writeInt32LE(SY, 4);
size.writeInt32LE(SZ, 8);
const xyzi = Buffer.alloc(4 + voxels.length);
xyzi.writeInt32LE(voxels.length / 4, 0);
Buffer.from(voxels).copy(xyzi, 4);
const rgba = Buffer.alloc(1024);
PALETTE.forEach((p, i) => {
  if (!p) return;
  const c = parseInt(p[0], 16);
  rgba.set([(c >> 16) & 255, (c >> 8) & 255, c & 255, 255], (i - 1) * 4); // Eintrag i - 1 = Farbindex i
});

const children = Buffer.concat([chunk('SIZE', size), chunk('XYZI', xyzi), chunk('RGBA', rgba)]);
const main = Buffer.alloc(12);
main.write('MAIN', 0, 'ascii');
main.writeInt32LE(0, 4);
main.writeInt32LE(children.length, 8);
const header = Buffer.alloc(8);
header.write('VOX ', 0, 'ascii');
header.writeInt32LE(150, 4);

const out = process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), 'testmap.vox');
writeFileSync(out, Buffer.concat([header, main, children]));
console.log(`${out}: ${voxels.length / 4} Voxel`);
