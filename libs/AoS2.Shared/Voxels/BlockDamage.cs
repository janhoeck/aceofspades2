using System.Collections.Generic;

namespace AoS2.Shared.Voxels
{
    /// <summary>Position eines Blocks.</summary>
    public struct BlockPos
    {
        public int X, Y, Z;

        public BlockPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public override string ToString() => $"({X}, {Y}, {Z})";
    }

    /// <summary>
    /// Schaden an Blöcken (Kugeln, Explosionen). Gespeichert wird nur für beschädigte Blöcke, die meisten sind heil.
    /// Erreicht der Schaden die Lebenspunkte des Blocktyps (<c>health</c> in blocks.json), ist der Block zerstört.
    /// Ändert sich ein Block (abgebaut, gesetzt, fällt), ist sein Schaden vergessen.
    /// </summary>
    public sealed class BlockDamage
    {
        private readonly VoxelWorld _world;
        private readonly BlockRegistry _registry;
        private readonly Dictionary<long, float> _damage = new Dictionary<long, float>();

        public BlockDamage(VoxelWorld world, BlockRegistry registry)
        {
            _world = world;
            _registry = registry;
            world.BlockChanged += (x, y, z, _, __) => _damage.Remove(Key(x, y, z));
        }

        public int Count => _damage.Count;

        /// <summary>Allen Schaden vergessen (Neustart eines Matches).</summary>
        public void Clear() => _damage.Clear();

        public float Get(int x, int y, int z) => _damage.TryGetValue(Key(x, y, z), out var d) ? d : 0;

        /// <summary>
        /// Fügt Schaden hinzu. <c>true</c>, wenn der Block damit zerstört ist (er bleibt stehen, bis der Aufrufer ihn entfernt).
        /// Luft und unzerstörbare Blöcke nehmen keinen Schaden.
        /// </summary>
        public bool Add(int x, int y, int z, float amount)
        {
            var id = _world.GetBlock(x, y, z);
            if (id == BlockRegistry.Air || _registry.IsIndestructible(id) || amount <= 0) return false;
            var health = _registry.Get(id)?.Health ?? 0;
            var key = Key(x, y, z);
            var total = (_damage.TryGetValue(key, out var d) ? d : 0) + amount;
            if (total >= health)
            {
                _damage.Remove(key);
                return true;
            }
            _damage[key] = total;
            return false;
        }

        /// <summary>Schaden als Stufe 1 … 255 (Anteil an den Lebenspunkten), 0 = heil. So geht er übers Netz.</summary>
        public byte Level(int x, int y, int z)
        {
            var d = Get(x, y, z);
            if (d <= 0) return 0;
            var health = _registry.Get(_world.GetBlock(x, y, z))?.Health ?? 0;
            if (health <= 0) return 0;
            var level = (int)(d / health * 255f);
            return (byte)(level < 1 ? 1 : level > 255 ? 255 : level);
        }

        /// <summary>Alle beschädigten Blöcke (für den Beitritt eines Spielers).</summary>
        public IEnumerable<BlockPos> All()
        {
            foreach (var key in _damage.Keys) yield return Unpack(key);
        }

        private static long Key(int x, int y, int z) => ((long)x << 42) | ((long)y << 21) | (uint)z;

        private static BlockPos Unpack(long key) => new BlockPos((int)(key >> 42), (int)((key >> 21) & 0x1FFFFF), (int)(key & 0x1FFFFF));
    }
}
