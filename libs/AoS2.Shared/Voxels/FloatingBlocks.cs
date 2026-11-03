using System.Collections.Generic;

namespace AoS2.Shared.Voxels
{
    /// <summary>Ein Block einer Gruppe: Position in der Welt und Blocktyp.</summary>
    public struct GroupBlock
    {
        public int X, Y, Z;
        public byte Id;

        public GroupBlock(int x, int y, int z, byte id)
        {
            X = x;
            Y = y;
            Z = z;
            Id = id;
        }
    }

    /// <summary>
    /// Findet Blockgruppen, die nach dem Entfernen eines Blocks keine Verbindung zum Boden mehr haben.
    /// Verbunden heißt: über gemeinsame Seiten (nicht nur Kanten oder Ecken). Boden ist die unterste Blockschicht (y = 0).
    ///
    /// Verfahren: Flutfüllung (Flood Fill) von jedem Nachbarn des entfernten Blocks aus. Sie sucht zuerst nach unten,
    /// weil der Boden meist dort liegt, und bricht ab, sobald sie den Boden erreicht. Erreicht sie mehr als
    /// <c>maxGroupSize</c> Blöcke, gilt die Gruppe als verankert: So bleibt eine einzelne Prüfung immer billig,
    /// und riesige Strukturen fallen nicht auf einmal herunter.
    /// </summary>
    public static class FloatingBlocks
    {
        public const int DefaultMaxGroupSize = 8192;

        // Reihenfolge der Nachbarn: unten zuletzt auf den Stapel, damit es zuerst nach unten geht.
        private static readonly int[] OrderX = { 0, 1, -1, 0, 0, 0 };
        private static readonly int[] OrderY = { 1, 0, 0, 0, 0, -1 };
        private static readonly int[] OrderZ = { 0, 0, 0, 1, -1, 0 };

        /// <summary>
        /// Gruppen, die nach einer Änderung an (x, y, z) frei schweben (Block dort ist schon entfernt).
        /// Die Blöcke bleiben in der Welt; entfernen erledigt <see cref="FallingBlocks"/>.
        /// </summary>
        public static List<List<GroupBlock>> FindDetached(VoxelWorld world, int x, int y, int z, int maxGroupSize = DefaultMaxGroupSize)
        {
            var result = new List<List<GroupBlock>>();
            // Blöcke, deren Verbleib schon geklärt ist (verankert oder schon Teil einer gefundenen Gruppe).
            var settled = new HashSet<long>();
            CheckNeighbours(world, x, y, z, maxGroupSize, settled, result);
            return result;
        }

        /// <summary>
        /// Wie <see cref="FindDetached(VoxelWorld, int, int, int, int)"/> für viele entfernte Blöcke auf einmal (Explosion):
        /// Jeder Nachbarblock wird nur einmal untersucht, auch wenn er an mehrere entfernte Blöcke grenzt.
        /// </summary>
        public static List<List<GroupBlock>> FindDetached(VoxelWorld world, IReadOnlyList<BlockPos> removed, int maxGroupSize = DefaultMaxGroupSize)
        {
            var result = new List<List<GroupBlock>>();
            var settled = new HashSet<long>();
            foreach (var p in removed) CheckNeighbours(world, p.X, p.Y, p.Z, maxGroupSize, settled, result);
            return result;
        }

        private static void CheckNeighbours(VoxelWorld world, int x, int y, int z, int maxGroupSize, HashSet<long> settled, List<List<GroupBlock>> result)
        {
            for (var f = 0; f < BlockFaces.Count; f++)
            {
                int nx = x + BlockFaces.DirX[f], ny = y + BlockFaces.DirY[f], nz = z + BlockFaces.DirZ[f];
                if (!world.IsSolid(nx, ny, nz) || settled.Contains(Key(nx, ny, nz))) continue;

                var visited = new HashSet<long>();
                var group = Flood(world, nx, ny, nz, maxGroupSize, visited);
                settled.UnionWith(visited);
                if (group != null) result.Add(group);
            }
        }

        /// <summary>Hängt der Block (x, y, z) mit dem Boden zusammen? (Luft gilt als nicht verbunden.)</summary>
        public static bool IsAnchored(VoxelWorld world, int x, int y, int z, int maxGroupSize = DefaultMaxGroupSize) =>
            world.IsSolid(x, y, z) && Flood(world, x, y, z, maxGroupSize, new HashSet<long>()) == null;

        /// <summary>Liefert die schwebende Gruppe ab einem Startblock oder <c>null</c>, wenn sie verankert ist.</summary>
        private static List<GroupBlock> Flood(VoxelWorld world, int sx, int sy, int sz, int maxGroupSize, HashSet<long> visited)
        {
            var stack = new Stack<long>();
            var group = new List<GroupBlock>();
            stack.Push(Key(sx, sy, sz));
            visited.Add(Key(sx, sy, sz));

            while (stack.Count > 0)
            {
                Unpack(stack.Pop(), out var x, out var y, out var z);
                if (y == 0) return null; // Boden erreicht
                group.Add(new GroupBlock(x, y, z, world.GetBlock(x, y, z)));
                if (group.Count > maxGroupSize) return null; // zu groß: gilt als verankert

                for (var i = 0; i < OrderX.Length; i++)
                {
                    int nx = x + OrderX[i], ny = y + OrderY[i], nz = z + OrderZ[i];
                    if (!world.IsSolid(nx, ny, nz)) continue;
                    var key = Key(nx, ny, nz);
                    if (visited.Add(key)) stack.Push(key);
                }
            }
            return group;
        }

        // Schlüssel aus drei Koordinaten (je 21 Bit, reicht für Welten bis 2 Mio. Blöcke Kantenlänge).
        private static long Key(int x, int y, int z) => ((long)x << 42) | ((long)y << 21) | (uint)z;

        private static void Unpack(long key, out int x, out int y, out int z)
        {
            x = (int)(key >> 42);
            y = (int)((key >> 21) & 0x1FFFFF);
            z = (int)(key & 0x1FFFFF);
        }
    }
}
