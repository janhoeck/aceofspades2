/**
 * The voxel battlefield from the launcher (launcher/AoS2.Launcher/Controls/Battlefield.cs), computed
 * block for block the same way: terrain with grass and dirt, blue forts on the left, green on the right,
 * a crater with loose blocks in the middle and a few clouds on top. The same seed always gives the same picture.
 */

export interface Block {
  x: number;
  y: number;
  color: string;
}

export const SKY = "#1A2333";
const CLOUD = "#222D40";
const GRASS = "#7E9A4A";
const DIRT = "#6B5236";
const DEEP_DIRT = "#4A3926";
const BLUE = "#2F6FE4";
const BLUE_DARK = "#2659B8";
const GREEN = "#3FA34D";
const GREEN_DARK = "#33853E";

/** Simple, reproducible randomness (same formula as the launcher). */
function lehmer(seed: number) {
  let state = ((seed * 7919) % 2147483646) + 1;
  return () => {
    state = (state * 16807) % 2147483647;
    return state / 2147483647;
  };
}

export function generateBattlefield(columns: number, rows: number, seed: number): Block[] {
  const blocks: Block[] = [];
  const random = lehmer(seed);
  const put = (x: number, y: number, color: string) => {
    if (x >= 0 && x < columns && y >= 0 && y < rows) blocks.push({ x, y, color });
  };

  for (let i = 0; i < 6; i++) {
    const cx = Math.floor(random() * columns);
    const cy = 1 + Math.floor(random() * 3);
    const length = 3 + Math.floor(random() * 5);
    for (let k = 0; k < length; k++) put(cx + k, cy, CLOUD);
  }

  // Terrain height per column, with a crater in the middle.
  const ground: number[] = [];
  for (let x = 0; x < columns; x++) {
    const distance = Math.abs(x / columns - 0.5);
    let g = rows - 5 + roundHalfEven(Math.sin(x * 0.35) + Math.sin(x * 0.11) * 1.5);
    if (distance < 0.1) g += roundHalfEven((0.1 - distance) * 40);
    ground.push(Math.min(rows - 1, g));
  }
  for (let x = 0; x < columns; x++)
    for (let y = ground[x]; y < rows; y++) put(x, y, y === ground[x] ? GRASS : y < ground[x] + 3 ? DIRT : DEEP_DIRT);

  const fort = (x0: number, width: number, height: number, c1: string, c2: string) => {
    for (let x = Math.max(0, x0); x < Math.min(columns, x0 + width); x++) {
      const g = ground[x];
      const h = height + ((x - x0) % 3 === 0 ? 1 : 0); // battlements
      for (let y = g - h; y < g; y++) {
        // bullet holes
        if (x > x0 + 1 && x < x0 + width - 2 && y > g - height + 2 && y < g - 1 && (x + y) % 5 === 0) continue;
        put(x, y, (x + y) % 3 !== 0 ? c1 : c2);
      }
    }
  };
  fort(4, 9, 7, BLUE, BLUE_DARK);
  fort(15, 6, 4, BLUE, BLUE_DARK);
  fort(columns - 13, 9, 7, GREEN, GREEN_DARK);
  fort(columns - 21, 6, 4, GREEN, GREEN_DARK);

  for (let i = 0; i < 16; i++) {
    const x = Math.floor(columns * (0.4 + random() * 0.2));
    const y = ground[x] - 1 - Math.floor(random() * 5);
    put(x, y, random() < 0.5 ? DIRT : GRASS);
  }

  return blocks;
}

/** .NET's Math.Round rounds half to even by default; doing the same keeps the picture identical. */
function roundHalfEven(value: number): number {
  const floor = Math.floor(value);
  const diff = value - floor;
  if (diff > 0.5) return floor + 1;
  if (diff < 0.5) return floor;
  return floor % 2 === 0 ? floor : floor + 1;
}
