using System;

namespace AoS2.Shared.Voxels
{
    /// <summary>Wird bei jeder Blockänderung aufgerufen (alter und neuer Typ).</summary>
    public delegate void BlockChangedHandler(int x, int y, int z, byte oldId, byte newId);

    /// <summary>
    /// Die Blockwelt: pro Block eine Blocktyp-ID (1 Byte, 0 = Luft), aufgeteilt in Chunks à 32 × 32 × 32.
    /// Block (x, y, z) füllt den Würfel [x, x+1) × [y, y+1) × [z, z+1); y zeigt nach oben.
    /// Chunks, die nur Luft enthalten, belegen keinen Speicher.
    /// </summary>
    public sealed class VoxelWorld
    {
        public const int ChunkSize = 32;
        public const int ChunkShift = 5;
        public const int ChunkMask = ChunkSize - 1;
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        private readonly byte[][] _chunks;

        public VoxelWorld(int sizeX, int sizeY, int sizeZ)
        {
            if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) throw new ArgumentException("Die Welt muss in jeder Richtung mindestens einen Block groß sein.");
            if ((long)sizeX * sizeY * sizeZ > 1L << 28) throw new ArgumentException("Die Welt ist zu groß.");
            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = sizeZ;
            ChunksX = (sizeX + ChunkMask) >> ChunkShift;
            ChunksY = (sizeY + ChunkMask) >> ChunkShift;
            ChunksZ = (sizeZ + ChunkMask) >> ChunkShift;
            _chunks = new byte[ChunksX * ChunksY * ChunksZ][];
        }

        public int SizeX { get; }
        public int SizeY { get; }
        public int SizeZ { get; }
        public int ChunksX { get; }
        public int ChunksY { get; }
        public int ChunksZ { get; }

        public event BlockChangedHandler BlockChanged;

        /// <summary>Index eines Blocks innerhalb seines Chunks (x läuft am schnellsten, dann z, dann y).</summary>
        public static int LocalIndex(int lx, int ly, int lz) => (ly << (2 * ChunkShift)) | (lz << ChunkShift) | lx;

        public bool IsInside(int x, int y, int z) =>
            (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

        /// <summary>Blocktyp an einer Stelle; außerhalb der Welt Luft.</summary>
        public byte GetBlock(int x, int y, int z)
        {
            if (!IsInside(x, y, z)) return BlockRegistry.Air;
            var chunk = _chunks[ChunkIndex(x >> ChunkShift, y >> ChunkShift, z >> ChunkShift)];
            return chunk == null ? BlockRegistry.Air : chunk[LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask)];
        }

        public bool IsSolid(int x, int y, int z) => GetBlock(x, y, z) != BlockRegistry.Air;

        /// <summary>
        /// Fester Block für Bewegung und Kollision: Unter der Welt und an den Seitenrändern ist eine
        /// unsichtbare Wand, nach oben ist die Welt offen.
        /// </summary>
        public bool IsSolidForMovement(int x, int y, int z)
        {
            if (y < 0 || (uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) return true;
            return y < SizeY && GetBlock(x, y, z) != BlockRegistry.Air;
        }

        /// <summary>Setzt einen Block und meldet die Änderung über <see cref="BlockChanged"/>.</summary>
        public void SetBlock(int x, int y, int z, byte id)
        {
            if (!IsInside(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x), $"Block ({x}, {y}, {z}) liegt außerhalb der Welt.");
            var index = ChunkIndex(x >> ChunkShift, y >> ChunkShift, z >> ChunkShift);
            var chunk = _chunks[index];
            if (chunk == null)
            {
                if (id == BlockRegistry.Air) return;
                chunk = _chunks[index] = new byte[ChunkVolume];
            }
            var local = LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask);
            var old = chunk[local];
            if (old == id) return;
            chunk[local] = id;
            BlockChanged?.Invoke(x, y, z, old, id);
        }

        /// <summary>
        /// Rohdaten eines Chunks (32³ Bytes, Index siehe <see cref="LocalIndex"/>) oder <c>null</c>, wenn er nur Luft enthält.
        /// Nur lesen – Änderungen am Array umgehen <see cref="BlockChanged"/>.
        /// </summary>
        public byte[] GetChunkData(int cx, int cy, int cz) => _chunks[ChunkIndex(cx, cy, cz)];

        /// <summary>Ersetzt einen ganzen Chunk ohne Ereignisse (zum Laden von Maps). <c>null</c> = nur Luft.</summary>
        public void SetChunkData(int cx, int cy, int cz, byte[] data)
        {
            if (data != null && data.Length != ChunkVolume) throw new ArgumentException("Ein Chunk hat genau 32³ Blöcke.", nameof(data));
            _chunks[ChunkIndex(cx, cy, cz)] = data;
        }

        /// <summary>
        /// Kopiert einen Quader (Ursprung x0/y0/z0, Größe sx/sy/sz) nach <paramref name="dest"/>,
        /// Index <c>(y * sz + z) * sx + x</c>. Außerhalb der Welt steht Luft.
        /// </summary>
        public void CopyRegion(int x0, int y0, int z0, int sx, int sy, int sz, byte[] dest)
        {
            if (dest.Length < sx * sy * sz) throw new ArgumentException("Zielpuffer ist zu klein.", nameof(dest));
            var i = 0;
            for (var y = y0; y < y0 + sy; y++)
            {
                for (var z = z0; z < z0 + sz; z++)
                {
                    if ((uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ)
                    {
                        Array.Clear(dest, i, sx);
                        i += sx;
                        continue;
                    }
                    var x = x0;
                    var end = x0 + sx;
                    while (x < end)
                    {
                        if ((uint)x >= (uint)SizeX)
                        {
                            dest[i++] = BlockRegistry.Air;
                            x++;
                            continue;
                        }
                        // Ein Stück der Zeile, das im selben Chunk liegt, am Stück kopieren.
                        var runEnd = Math.Min(Math.Min(end, SizeX), (x | ChunkMask) + 1);
                        var chunk = _chunks[ChunkIndex(x >> ChunkShift, y >> ChunkShift, z >> ChunkShift)];
                        var count = runEnd - x;
                        if (chunk == null) Array.Clear(dest, i, count);
                        else Buffer.BlockCopy(chunk, LocalIndex(x & ChunkMask, y & ChunkMask, z & ChunkMask), dest, i, count);
                        i += count;
                        x = runEnd;
                    }
                }
            }
        }

        private int ChunkIndex(int cx, int cy, int cz) => (cy * ChunksZ + cz) * ChunksX + cx;
    }
}
