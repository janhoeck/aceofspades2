// Erzeugt aus einer Grund-Textur und ihrer Teamfarben-Maske je eine fertig
// eingefärbte Textur pro Team: farbe = basis * lerp(1, teamfarbe, maske).
//
// Aufruf:  node assets-src/tools/bake-team-textures.mjs <basis.png> <maske.png>
// Ergebnis: <basis>_blue.png und <basis>_green.png neben der Basis-Datei.
//
// Ohne externe Pakete: liest/schreibt 8-Bit-PNGs (RGB/RGBA, ohne Interlacing).

import { readFileSync, writeFileSync } from 'node:fs';
import { inflateSync, deflateSync } from 'node:zlib';

const TEAMS = {
  blue: [70, 130, 255],
  green: [90, 220, 90],
};

function readPng(path) {
  const buf = readFileSync(path);
  let pos = 8, width = 0, height = 0, colorType = 0;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos);
    const type = buf.toString('ascii', pos + 4, pos + 8);
    const data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      colorType = data[9];
      if (data[8] !== 8 || data[12] !== 0 || (colorType !== 2 && colorType !== 6)) {
        throw new Error(`${path}: nur 8-Bit RGB/RGBA ohne Interlacing wird unterstützt`);
      }
    } else if (type === 'IDAT') idat.push(data);
    pos += 12 + len;
  }
  const bpp = colorType === 6 ? 4 : 3;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * bpp;
  const out = Buffer.alloc(width * height * 4);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
      let p = 0;
      if (filter === 1) p = a;
      else if (filter === 2) p = b;
      else if (filter === 3) p = (a + b) >> 1;
      else if (filter === 4) {
        const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c);
        p = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }
      line[x] = (line[x] + p) & 255;
    }
    for (let x = 0; x < width; x++) {
      const o = (y * width + x) * 4;
      out[o] = line[x * bpp]; out[o + 1] = line[x * bpp + 1]; out[o + 2] = line[x * bpp + 2];
      out[o + 3] = bpp === 4 ? line[x * bpp + 3] : 255;
    }
    prev = line;
  }
  return { width, height, data: out };
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 255] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'ascii');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, crc]);
}
function writePng(path, { width, height, data }) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 6; // 8 Bit, RGBA
  const raw = Buffer.alloc(height * (width * 4 + 1));
  for (let y = 0; y < height; y++) {
    data.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  writeFileSync(path, Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]));
}

const [basePath, maskPath] = process.argv.slice(2);
if (!basePath || !maskPath) {
  console.error('Aufruf: node bake-team-textures.mjs <basis.png> <maske.png>');
  process.exit(1);
}
const base = readPng(basePath);
const mask = readPng(maskPath);
if (base.width !== mask.width || base.height !== mask.height) {
  throw new Error('Basis und Maske müssen gleich groß sein');
}
for (const [team, color] of Object.entries(TEAMS)) {
  const out = Buffer.from(base.data);
  for (let i = 0; i < out.length; i += 4) {
    const m = mask.data[i] / 255;
    for (let ch = 0; ch < 3; ch++) {
      out[i + ch] = Math.round(out[i + ch] * (1 + (color[ch] / 255 - 1) * m));
    }
  }
  const target = basePath.replace(/\.png$/i, `_${team}.png`);
  writePng(target, { ...base, data: out });
  console.log(`geschrieben: ${target}`);
}
