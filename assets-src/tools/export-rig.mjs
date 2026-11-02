// Exportiert Skelett, Trefferzonen und Animationen eines Blockbench-Modells als
// JSON für Client und Server. Beide berechnen daraus dieselbe Pose, dadurch passen
// die Trefferzonen immer exakt zum sichtbaren Modell.
//
// Aufruf:  node assets-src/tools/export-rig.mjs <modell.bbmodel> <ziel.json>
//          node assets-src/tools/export-rig.mjs <modell.bbmodel> --check <animation> <zeit> [klasse]
//
// Jeder Würfel des Modells wird zu einer Trefferbox an seinem Knochen. Die
// Animationen werden mit Blockbenchs eigener Interpolation (Catmull-Rom wie in
// Blockbench 5) mit 60 Bildern pro Sekunde abgetastet; zwischen zwei Bildern wird
// linear interpoliert.

import { readFileSync, writeFileSync } from 'node:fs';

const FPS = 60;
const STANCES = {
  stand: ['idle', 'walk', 'run'],
  crouch: ['crouch', 'crouch_walk'],
  air: ['jump', 'fall'],
};

// ---------- Modell einlesen ----------

function loadModel(path) {
  const m = JSON.parse(readFileSync(path, 'utf8'));
  const groupsById = new Map((m.groups || []).map(g => [g.uuid, g]));
  const elementsById = new Map(m.elements.map(e => [e.uuid, e]));
  const bones = [];
  const boxes = [];

  function walk(node, parent, gearClass) {
    const g = groupsById.get(node.uuid);
    const name = g.name;
    const gear = /^gear_([a-z]+)_/.exec(name)?.[1] ?? gearClass;
    bones.push({ name, parent: parent?.name ?? null, pivot: g.origin.slice(), rotation: (g.rotation || [0, 0, 0]).slice() });
    for (const child of node.children || []) {
      if (typeof child === 'string') {
        const e = elementsById.get(child);
        if (!e || e.type && e.type !== 'cube') continue;
        const inf = e.inflate || 0;
        if ((e.rotation || [0, 0, 0]).some(v => v !== 0)) {
          throw new Error(`Würfel ${e.name}: gedrehte Würfel werden nicht unterstützt (Drehung über eine Gruppe lösen)`);
        }
        boxes.push({
          name: e.name,
          bone: name,
          from: e.from.map(v => v - inf),
          to: e.to.map(v => v + inf),
          gear: gear ?? null,
        });
      } else {
        walk(child, { name }, gear);
      }
    }
  }
  for (const node of m.outliner) {
    if (typeof node !== 'string') walk(node, null, null);
  }

  const zoneOf = new Map();
  for (const b of bones) {
    const parentZone = b.parent ? zoneOf.get(b.parent) : null;
    let zone = parentZone;
    if (b.name === 'head') zone = 'head';
    else if (b.name === 'body') zone = 'torso';
    else if (/^arm_(right|left)$/.test(b.name)) zone = 'arm';
    else if (/^leg_(right|left)$/.test(b.name)) zone = 'leg';
    zoneOf.set(b.name, zone ?? null);
  }
  for (const box of boxes) box.zone = zoneOf.get(box.bone);

  const animations = (m.animations || []).map(a => {
    const tracks = {};
    for (const [uuid, an] of Object.entries(a.animators || {})) {
      const bone = groupsById.get(uuid)?.name;
      if (!bone || !an.keyframes?.length) continue;
      for (const kf of an.keyframes) {
        const ch = kf.channel;
        (tracks[bone] ??= {});
        (tracks[bone][ch] ??= []).push({
          time: kf.time,
          interpolation: kf.interpolation || 'linear',
          value: ['x', 'y', 'z'].map(k => parseFloat(kf.data_points[0][k]) || 0),
        });
      }
    }
    for (const t of Object.values(tracks)) for (const ch of Object.values(t)) ch.sort((p, q) => p.time - q.time);
    return { name: a.name, length: a.length, loop: a.loop, tracks };
  });

  return { bones, boxes, animations };
}

// ---------- Interpolation wie in Blockbench ----------

function catmullRom(t, p0, p1, p2, p3) {
  const v0 = (p2 - p0) * 0.5, v1 = (p3 - p1) * 0.5, t2 = t * t, t3 = t * t2;
  return (2 * p1 - 2 * p2 + v0 + v1) * t3 + (-3 * p1 + 3 * p2 - 2 * v0 - v1) * t2 + v0 * t + p1;
}
// entspricht THREE.SplineCurve.getPoint(t).y, wie Blockbench es für Catmull-Rom nutzt
function splineY(points, t) {
  const p = (points.length - 1) * t;
  const i = Math.floor(p), w = p - i;
  const p0 = points[i === 0 ? i : i - 1];
  const p1 = points[i];
  const p2 = points[i > points.length - 2 ? points.length - 1 : i + 1];
  const p3 = points[i > points.length - 3 ? points.length - 1 : i + 2];
  return catmullRom(w, p0, p1, p2, p3);
}
// loopLength: Länge einer Schleifen-Animation, sonst null. Bei Schleifen nimmt
// Blockbench als Nachbarn am Rand den Keyframe vom anderen Ende der Schleife.
function sampleChannel(keys, time, loopLength = null) {
  if (time <= keys[0].time) return keys[0].value.slice();
  const last = keys[keys.length - 1];
  if (time >= last.time) return last.value.slice();
  let k = 0;
  while (keys[k + 1].time <= time) k++;
  const before = keys[k], after = keys[k + 1];
  const alpha = (time - before.time) / (after.time - before.time);
  if (before.interpolation === 'catmullrom' || after.interpolation === 'catmullrom') {
    let bp = keys[k - 1], ap = keys[k + 2];
    if (loopLength !== null && keys.length > 2) {
      const closed = keys[0].time === 0 && Math.abs(last.time - loopLength) < 1e-6;
      if (!bp) bp = closed ? keys[keys.length - 2] : last;
      if (!ap) ap = closed ? keys[1] : keys[0];
    }
    return [0, 1, 2].map(ax => {
      const pts = [];
      if (bp) pts.push(bp.value[ax]);
      pts.push(before.value[ax], after.value[ax]);
      if (ap) pts.push(ap.value[ax]);
      return splineY(pts, (alpha + (bp ? 1 : 0)) / (pts.length - 1));
    });
  }
  if (before.interpolation === 'step') return before.value.slice();
  return before.value.map((v, ax) => v + (after.value[ax] - v) * alpha);
}

// ---------- Pose berechnen ----------

const DEG = Math.PI / 180;
function mul(a, b) {
  const r = new Array(16).fill(0);
  for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) for (let k = 0; k < 4; k++) r[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j];
  return r;
}
function translate([x, y, z]) { return [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1]; }
// Drehreihenfolge ZYX wie Blockbench (Matrix = Rz * Ry * Rx), Winkel in Grad
function rotateZYX([x, y, z]) {
  const [cx, sx, cy, sy, cz, sz] = [Math.cos(x * DEG), Math.sin(x * DEG), Math.cos(y * DEG), Math.sin(y * DEG), Math.cos(z * DEG), Math.sin(z * DEG)];
  const rx = [1, 0, 0, 0, 0, cx, -sx, 0, 0, sx, cx, 0, 0, 0, 0, 1];
  const ry = [cy, 0, sy, 0, 0, 1, 0, 0, -sy, 0, cy, 0, 0, 0, 0, 1];
  const rz = [cz, -sz, 0, 0, sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  return mul(rz, mul(ry, rx));
}
function apply(m, [x, y, z]) {
  return [m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]];
}

// lokale Werte je Knochen -> Weltmatrix je Knochen (Modellraum, Pixel)
function boneMatrices(rig, local) {
  const world = new Map();
  for (const b of rig.bones) {
    const parent = b.parent ? rig.bones.find(p => p.name === b.parent) : null;
    const l = local[b.name] || {};
    const rot = b.rotation.map((v, i) => v + (l.rotation?.[i] ?? 0));
    const pos = l.position ?? [0, 0, 0];
    const offset = b.pivot.map((v, i) => v - (parent ? parent.pivot[i] : 0) + pos[i]);
    const m = mul(translate(offset), rotateZYX(rot));
    world.set(b.name, parent ? mul(world.get(parent.name), m) : m);
  }
  return world;
}
function boxCorners(rig, world, box) {
  const b = rig.bones.find(x => x.name === box.bone);
  const out = [];
  for (const x of [box.from[0], box.to[0]]) for (const y of [box.from[1], box.to[1]]) for (const z of [box.from[2], box.to[2]]) {
    out.push(apply(world.get(box.bone), [x - b.pivot[0], y - b.pivot[1], z - b.pivot[2]]));
  }
  return out;
}
function localPose(anim, time) {
  const local = {};
  for (const [bone, chans] of Object.entries(anim.tracks)) {
    local[bone] = {};
    for (const [ch, keys] of Object.entries(chans)) local[bone][ch] = sampleChannel(keys, time, anim.loop === 'loop' ? anim.length : null);
  }
  return local;
}

// ---------- Ausgabe ----------

const round = v => Math.round(v * 1000) / 1000;
function visibleBoxes(rig, cls) { return rig.boxes.filter(b => b.gear === null || b.gear === cls); }
function extents(rig, anim, cls) {
  let maxY = -Infinity, minY = Infinity, maxR = 0;
  const frames = Math.round(anim.length * FPS);
  for (let f = 0; f <= frames; f++) {
    const world = boneMatrices(rig, localPose(anim, f / FPS));
    for (const box of visibleBoxes(rig, cls)) for (const [x, y, z] of boxCorners(rig, world, box)) {
      maxY = Math.max(maxY, y); minY = Math.min(minY, y); maxR = Math.max(maxR, Math.hypot(x, z));
    }
  }
  return { maxY, minY, maxR };
}

const [modelPath, second, ...rest] = process.argv.slice(2);
if (!modelPath || !second) {
  console.error('Aufruf: node export-rig.mjs <modell.bbmodel> <ziel.json>\n       node export-rig.mjs <modell.bbmodel> --check <animation> <zeit> [klasse]');
  process.exit(1);
}
const rig = loadModel(modelPath);
const classes = [...new Set(rig.boxes.map(b => b.gear).filter(Boolean))];

if (second === '--check') {
  const [animName, timeStr, cls = null] = rest;
  const anim = rig.animations.find(a => a.name === animName);
  const world = boneMatrices(rig, localPose(anim, parseFloat(timeStr)));
  const report = {};
  let maxY = -Infinity, minY = Infinity;
  for (const box of visibleBoxes(rig, cls)) {
    const c = boxCorners(rig, world, box);
    const ys = c.map(p => p[1]);
    maxY = Math.max(maxY, ...ys); minY = Math.min(minY, ...ys);
    if (['nose', 'boot_right', 'boot_left', 'glove_right'].includes(box.name)) {
      report[box.name] = [0, 1, 2].map(i => round(c.reduce((s, p) => s + p[i], 0) / 8));
    }
  }
  console.log(JSON.stringify({ animation: animName, time: parseFloat(timeStr), class: cls, maxY: round(maxY), minY: round(minY), centers: report }));
  process.exit(0);
}

const stances = {};
for (const [stance, names] of Object.entries(STANCES)) {
  stances[stance] = {};
  for (const cls of [null, ...classes]) {
    let maxY = -Infinity, maxR = 0;
    for (const n of names) {
      const a = rig.animations.find(x => x.name === n);
      if (!a) continue;
      const e = extents(rig, a, cls);
      maxY = Math.max(maxY, e.maxY); maxR = Math.max(maxR, e.maxR);
    }
    stances[stance][cls ?? 'none'] = { height_px: round(maxY), height_blocks: round(maxY / 16), radius_px: round(maxR) };
  }
}

const out = {
  source: modelPath.replace(/\\/g, '/'),
  units: { px_per_block: 16, forward: '-z', right: '+x', up: '+y' },
  rotation: { unit: 'deg', order: 'ZYX', matrix: 'Rz * Ry * Rx' },
  bone_transform: 'world(bone) = world(parent) * T(pivot - parent.pivot + position) * R(rest + rotation); Punkt im Knochen = p - pivot',
  sampling: { fps: FPS, between_frames: 'linear' },
  classes,
  bones: rig.bones.map(({ name, parent, pivot, rotation }) => ({ name, parent, pivot, rotation })),
  hitboxes: rig.boxes.map(b => ({ name: b.name, bone: b.bone, zone: b.zone, gear: b.gear, from: b.from.map(round), to: b.to.map(round) })),
  stances,
  animations: rig.animations.map(a => {
    const frames = Math.round(a.length * FPS) + 1;
    const tracks = {};
    for (const [bone, chans] of Object.entries(a.tracks)) {
      tracks[bone] = {};
      for (const [ch, keys] of Object.entries(chans)) {
        const flat = [];
        for (let f = 0; f < frames; f++) flat.push(...sampleChannel(keys, Math.min(f / FPS, a.length), a.loop === 'loop' ? a.length : null).map(round));
        tracks[bone][ch] = flat;
      }
    }
    return { name: a.name, length: a.length, loop: a.loop, frames, tracks };
  }),
};
writeFileSync(second, JSON.stringify(out));
console.log(`geschrieben: ${second} (${rig.bones.length} Knochen, ${rig.boxes.length} Trefferboxen, ${rig.animations.length} Animationen)`);
for (const [stance, v] of Object.entries(stances)) {
  console.log(`  ${stance}: ` + Object.entries(v).map(([c, e]) => `${c} ${e.height_blocks}`).join(', '));
}
