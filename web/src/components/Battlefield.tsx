"use client";

import { useLayoutEffect, useRef, useState } from "react";
import { generateBattlefield, SKY } from "./battlefield-generator";

const BLOCK = 16;

/** Below this the blue and green forts would overlap. Narrower areas show the middle. */
const MIN_COLUMNS = 44;

/**
 * Draws the battlefield as SVG, like the launcher: as many blocks as fit into the area, so the forts
 * always stand at the edges. Before the first measurement (server HTML) a default size is used.
 * Every block gets a light top edge and a dark right/bottom edge.
 */
export function Battlefield({ seed = 7, className }: { seed?: number; className?: string }) {
  const container = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ columns: 80, rows: 26 });

  useLayoutEffect(() => {
    const element = container.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      const columns = Math.max(MIN_COLUMNS, Math.ceil(entry.contentRect.width / BLOCK));
      const rows = Math.max(1, Math.ceil(entry.contentRect.height / BLOCK));
      setSize((current) => (current.columns === columns && current.rows === rows ? current : { columns, rows }));
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const { columns, rows } = size;
  const blocks = generateBattlefield(columns, rows, seed);
  const width = columns * BLOCK;
  const height = rows * BLOCK;

  return (
    <div ref={container} className={className} style={{ background: SKY }} aria-hidden="true">
      <svg className="h-full w-full" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="xMidYMax slice" shapeRendering="crispEdges">
        <defs>
          <g id="voxel-edges">
            <rect width={BLOCK} height={2} fill="#fff" fillOpacity={0.1} />
            <rect y={BLOCK - 2} width={BLOCK} height={2} fill="#000" fillOpacity={0.28} />
            <rect x={BLOCK - 2} width={2} height={BLOCK - 2} fill="#000" fillOpacity={0.28} />
          </g>
        </defs>
        {blocks.map(({ x, y, color }) => (
          <g key={`${x}-${y}`}>
            <rect x={x * BLOCK} y={y * BLOCK} width={BLOCK} height={BLOCK} fill={color} />
            <use href="#voxel-edges" x={x * BLOCK} y={y * BLOCK} />
          </g>
        ))}
      </svg>
    </div>
  );
}
