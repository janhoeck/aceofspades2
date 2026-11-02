// Erzeugt die 16x16-Blocktexturen prozedural: periodisches Rauschen (wiederholt
// sich nach genau 16 px, dadurch kachelbar), feste Farbrampen, kleine Details
// (Steinchen, Rindenfurchen, Splitt). Gleicher Zufallswert = gleiches Bild.
//
// Aufruf:  node assets-src/tools/gen-block-textures.mjs [ausgabeordner] [--vorschau <datei.png>]
// Ergebnis: block_*.png im Ausgabeordner (Standard: assets-src/textures/blocks).
//           Mit --vorschau zusätzlich ein Kontaktbogen: jede Textur 3x3 gekachelt, 6-fach vergrößert.
//
// Neue Variante: Eintrag in TEXTURES mit anderem Zufallswert ergänzen.
// Achtung: Überschreibt vorhandene Dateien gleichen Namens (auch von Hand nachbearbeitete).
//
// Ohne externe Pakete: schreibt 8-Bit-RGBA-PNGs.

import { mkdirSync, writeFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const N = 16;

// ---- Farbrampen (dunkel -> hell; gedeckt, Umgebung dunkler als Spieler) ----
const P = {
  dirt: ['2f2219', '3c2b1f', '4a3626', '5a4430', '6b5139'],
  stone: ['5b5247', '756a5c'],
  grass: ['33421f', '3f5126', '4b5f2d', '586c35', '67793f', '76874a'],
  bark: ['271c14', '35271c', '433326', '524030', '614d3a'],
  wood: ['5a3f28', '6d4f33', '7f5f3d', '8f6d47', 'a07c53'],
  leaves: ['1f2c18', '2a3a1f', '354827', '41552f', '4f6338', '5e7343'],
  asphalt: ['1e1e20', '27272a', '2e2e31', '353538', '3d3d40', '47474a'],
  aggregate: ['55534f', '64615b', '73706a'],
};

// ---- Hilfsfunktionen ----

const hex = h => [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
const wrap = v => ((v % N) + N) % N;

// Zufallsgenerator mit Startwert (mulberry32), liefert Zahlen in [0, 1)
function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// Periodische Value-Noise (Periode 16 px); cx/cy = Gitterzellen pro Achse
function noise(r, cx, cy = cx) {
  const g = Array.from({ length: cx * cy }, r);
  const at = (i, j) => g[(((j % cy) + cy) % cy) * cx + (((i % cx) + cx) % cx)];
  const s = t => t * t * (3 - 2 * t);
  return (x, y) => {
    const fx = (x + 0.5) / N * cx, fy = (y + 0.5) / N * cy;
    const i = Math.floor(fx), j = Math.floor(fy);
    const u = s(fx - i), v = s(fy - j);
    return (at(i, j) * (1 - u) + at(i + 1, j) * u) * (1 - v) + (at(i, j + 1) * (1 - u) + at(i + 1, j + 1) * u) * v;
  };
}

// Gewichtete Summe aus Noise-Ebenen [gewicht, zellenX, zellenY?]; zellenX = 0 bedeutet Pixelrauschen
function field(r, parts) {
  const layers = parts.map(([w, c, cy]) => [w, c === 0 ? null : noise(r, c, cy ?? c)]);
  const out = [];
  for (let y = 0; y < N; y++) {
    for (let x = 0; x < N; x++) out.push(layers.reduce((s, [w, f]) => s + w * (f ? f(x, y) : r()), 0));
  }
  return out;
}

// Feld nach Rang in Klassen 0..n-1 einteilen; dist = Anteil der Pixel je Klasse
function quantize(f, dist) {
  const order = f.map((v, i) => i).sort((a, b) => f[a] - f[b]);
  const out = new Array(f.length);
  let acc = 0, k = 0;
  const cum = dist.map(d => (acc += d));
  order.forEach((p, rank) => {
    while (k < cum.length - 1 && (rank + 0.5) / f.length > cum[k] / acc) k++;
    out[p] = k;
  });
  return out;
}

const img = () => ({ width: N, height: N, data: new Uint8Array(N * N * 4) });

// Koordinaten laufen über den Rand hinaus weiter (Kachel), damit Details die Naht nicht zerschneiden
function set(im, x, y, c, a = 255) {
  im.data.set([...(typeof c === 'string' ? hex(c) : c), a], (wrap(y) * N + wrap(x)) * 4);
}

function get(im, x, y) {
  const o = (wrap(y) * N + wrap(x)) * 4;
  return [...im.data.subarray(o, o + 4)];
}

function paint(im, classes, ramp) {
  classes.forEach((k, p) => set(im, p % N, (p / N) | 0, ramp[k]));
}

// ---- Texturen ----

function dirt(seed) {
  const r = rng(seed), im = img();
  paint(im, quantize(field(r, [[0.3, 4], [0.35, 8], [0.35, 0]]), [0.07, 0.25, 0.38, 0.22, 0.08]), P.dirt);
  // kleine Steinchen: Körper, Lichtkante oben links, Schatten unten rechts
  for (let i = 0; i < 4; i++) {
    const x = (r() * N) | 0, y = (r() * N) | 0, big = i < 2;
    set(im, x, y, big ? P.stone[1] : P.stone[0]);
    if (big) {
      set(im, x + 1, y, P.stone[0]);
      set(im, x + 1, y + 1, P.dirt[0]);
      set(im, x, y + 1, P.dirt[1]);
    } else set(im, x + 1, y + 1, P.dirt[1]);
  }
  return im;
}

function grassTop(seed) {
  const r = rng(seed), im = img();
  paint(im, quantize(field(r, [[0.22, 4], [0.36, 8], [0.42, 0]]), [0.06, 0.2, 0.34, 0.27, 0.13]), P.grass.slice(0, 5));
  // Halm-Spitzen: helle Pixel mit dunklem Schatten darunter
  for (let i = 0; i < 9; i++) {
    const x = (r() * N) | 0, y = (r() * N) | 0;
    set(im, x, y, P.grass[5]);
    set(im, x, y + 1, P.grass[1]);
  }
  return im;
}

// Erde mit unregelmäßigem Graskranz oben; nur waagerecht kachelbar (oben grenzt die Grasfläche an)
function grassSide(seed) {
  const r = rng(seed), im = dirt(seed + 1000), top = grassTop(seed + 2000);
  const dn = noise(r, 4, 1);
  const depth = [];
  for (let x = 0; x < N; x++) depth.push(2 + Math.round(dn(x, 0) * 2.4 + (r() < 0.25 ? 1 : 0)));
  for (let x = 0; x < N; x++) {
    const d = depth[x];
    for (let y = 0; y < d; y++) {
      const c = get(top, x, y + 5).slice(0, 3);
      set(im, x, y, y === 0 ? (r() < 0.5 ? P.grass[4] : P.grass[3]) : y === d - 1 ? P.grass[1] : c);
    }
    set(im, x, d, P.dirt[r() < 0.6 ? 0 : 1]); // Schattenkante unter dem Gras
  }
  return im;
}

function logSide(seed) {
  const r = rng(seed), im = img();
  paint(im, quantize(field(r, [[0.5, 8, 2], [0.25, 4, 1], [0.25, 0]]), [0.08, 0.22, 0.35, 0.25, 0.1]), P.bark);
  // senkrechte Rindenfurchen; Versätze summieren sich zu 0, damit die Kachel oben/unten passt
  for (const x0 of [1, 6, 11]) {
    let x = x0 + ((r() * 2) | 0);
    const steps = Array.from({ length: N }, () => 0);
    for (let k = 0; k < 2; k++) {
      steps[(r() * N) | 0] += 1;
      steps[(r() * N) | 0] -= 1;
    }
    const len = 10 + ((r() * 7) | 0), start = (r() * N) | 0;
    for (let i = 0; i < len; i++) {
      const y = start + i;
      x += steps[wrap(y)];
      set(im, x, y, P.bark[0]);
      set(im, x + 1, y, P.bark[1]);
      if (r() < 0.5) set(im, x - 1, y, P.bark[4]);
    }
  }
  return im;
}

// Stirnseite (oben/unten): Jahresringe mit Rindenrand
function logTop(seed) {
  const r = rng(seed), im = img(), wob = noise(r, 4);
  for (let y = 0; y < N; y++) {
    for (let x = 0; x < N; x++) {
      const dx = x - 7.5, dy = y - 7.5;
      // Mischung aus Kreis und Quadrat, leicht verwackelt
      const rr = 0.55 * Math.hypot(dx, dy) + 0.45 * Math.max(Math.abs(dx), Math.abs(dy)) + (wob(x, y) - 0.5) * 0.9;
      const edge = Math.min(x, y, 15 - x, 15 - y);
      let c;
      if (edge === 0) c = P.bark[r() < 0.5 ? 1 : 2];
      else if (edge === 1) c = r() < 0.35 ? P.bark[3] : P.wood[0];
      else if (rr < 1.4) c = P.wood[4];
      else if (rr < 2.3) c = P.wood[2];
      else if (rr < 3.6) c = P.wood[3];
      else if (rr < 4.4) c = P.wood[1];
      else if (rr < 5.6) c = P.wood[3];
      else if (rr < 6.3) c = P.wood[1];
      else c = P.wood[2];
      set(im, x, y, c);
    }
  }
  // Trocknungsriss vom Kern nach außen
  set(im, 9, 8, P.wood[0]);
  set(im, 10, 9, P.wood[0]);
  set(im, 11, 9, P.wood[1]);
  return im;
}

// Alpha nur 0 oder 255 (Cutout); durchsichtige Pixel tragen die Blattfarbe,
// damit Mipmaps keine dunklen Ränder erzeugen
function leaves(seed) {
  const r = rng(seed), im = img();
  const classes = quantize(field(r, [[0.4, 8], [0.3, 4], [0.3, 0]]), [0.17, 0.12, 0.2, 0.24, 0.18, 0.09]);
  const fill = hex(P.leaves[2]);
  classes.forEach((k, p) => {
    const x = p % N, y = (p / N) | 0;
    if (k === 0) set(im, x, y, fill, 0);
    else set(im, x, y, P.leaves[k - 1]);
  });
  // Blatt-Glanzlichter
  for (let i = 0; i < 7; i++) {
    const x = (r() * N) | 0, y = (r() * N) | 0;
    if (get(im, x, y)[3] && get(im, x + 1, y + 1)[3]) {
      set(im, x, y, P.leaves[5]);
      set(im, x + 1, y + 1, P.leaves[1]);
    }
  }
  return im;
}

function asphalt(seed) {
  const r = rng(seed), im = img();
  paint(im, quantize(field(r, [[0.25, 8], [0.15, 4], [0.6, 0]]), [0.05, 0.18, 0.3, 0.27, 0.15, 0.05]), P.asphalt);
  // Gesteinskörnung (Splitt) mit Schatten darunter
  for (let i = 0; i < 9; i++) {
    const x = (r() * N) | 0, y = (r() * N) | 0;
    set(im, x, y, P.aggregate[(r() * 3) | 0]);
    if (i < 3) set(im, x + 1, y, P.aggregate[0]);
    if (r() < 0.5) set(im, x, y + 1, P.asphalt[0]);
  }
  return im;
}

// Dateiname -> [Generator, Zufallswert]
const TEXTURES = {
  block_dirt: [dirt, 11],
  block_dirt_v2: [dirt, 12],
  block_grass_top: [grassTop, 21],
  block_grass_top_v2: [grassTop, 22],
  block_grass_side: [grassSide, 31],
  block_log_side: [logSide, 41],
  block_log_top: [logTop, 42],
  block_leaves: [leaves, 51],
  block_asphalt: [asphalt, 61],
  block_asphalt_v2: [asphalt, 62],
};

// ---- PNG schreiben ----

const CRC_TABLE = new Int32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c;
});

function crc32(buf) {
  let c = -1;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 255] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function writePng(path, { width, height, data }) {
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0; // Filter: keiner
    Buffer.from(data.buffer, data.byteOffset + y * width * 4, width * 4).copy(raw, y * (width * 4 + 1) + 1);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // Bit pro Kanal
  ihdr[9] = 6; // RGBA
  writeFileSync(path, Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('sRGB', Buffer.from([0])),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]));
}

// Kontaktbogen: jede Textur 3x3 gekachelt und vergrößert, Löcher zeigen Himmelblau
function writePreview(path, images) {
  const S = 6, size = 3 * N * S, cell = size + 16, cols = 5;
  const rows = Math.ceil(images.length / cols);
  const out = { width: cols * cell, height: rows * cell, data: new Uint8Array(cols * cell * rows * cell * 4) };
  for (let i = 0; i < out.width * out.height; i++) out.data.set([143, 179, 207, 255], i * 4);
  images.forEach((t, k) => {
    const ox = (k % cols) * cell + 8, oy = ((k / cols) | 0) * cell + 8;
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const o = ((((y / S) | 0) % N) * N + (((x / S) | 0) % N)) * 4;
        if (t.data[o + 3] < 128) continue;
        out.data.set([...t.data.subarray(o, o + 3), 255], ((oy + y) * out.width + ox + x) * 4);
      }
    }
  });
  writePng(path, out);
}

// ---- Hauptprogramm ----

const args = process.argv.slice(2);
const previewIdx = args.indexOf('--vorschau');
const previewPath = previewIdx >= 0 ? args.splice(previewIdx, 2)[1] : null;
if (previewIdx >= 0 && !previewPath) {
  console.error('Aufruf: node assets-src/tools/gen-block-textures.mjs [ausgabeordner] [--vorschau <datei.png>]');
  process.exit(1);
}
const outDir = args[0] ?? join(dirname(fileURLToPath(import.meta.url)), '..', 'textures', 'blocks');

mkdirSync(outDir, { recursive: true });
const images = [];
for (const [name, [gen, seed]] of Object.entries(TEXTURES)) {
  const im = gen(seed);
  writePng(join(outDir, `${name}.png`), im);
  images.push(im);
  console.log(`${name}.png`);
}
if (previewPath) {
  writePreview(previewPath, images);
  console.log(`Vorschau: ${previewPath} (Reihenfolge wie oben, 5 pro Zeile)`);
}
