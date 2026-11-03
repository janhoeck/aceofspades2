using System;
using System.Collections.Generic;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net.Client
{
    /// <summary>
    /// Blockänderungen, die der Client schon zeigt, bevor der Server sie bestätigt hat.
    /// <para>
    /// Für jede Stelle mit offener Vorhersage merkt er sich, was laut Server dort steht. Meldet der Server in der
    /// Zwischenzeit etwas für diese Stelle, wird nur dieser Wert aktualisiert (die Vorhersage bleibt sichtbar).
    /// Kommt die Antwort auf die eigene Anfrage, gilt wieder der Server-Wert: Bei Zustimmung ist das genau die
    /// vorhergesagte Änderung, bei Ablehnung verschwindet sie wieder.
    /// </para>
    /// Der Server schickt die Änderung immer vor der Antwort auf denselben geordneten Kanal; so stimmt die Reihenfolge.
    /// </summary>
    public sealed class PredictedBlocks
    {
        private sealed class Entry
        {
            public byte ServerId;
            public int Pending;
        }

        private readonly VoxelWorld _world;
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private readonly Dictionary<ushort, long> _pendingBySeq = new Dictionary<ushort, long>();

        public PredictedBlocks(VoxelWorld world)
        {
            _world = world;
        }

        /// <summary>Offene Anfragen ohne Antwort des Servers.</summary>
        public int PendingCount => _pendingBySeq.Count;

        public void Predict(ushort seq, int x, int y, int z, byte id)
        {
            var key = Key(x, y, z);
            if (_pendingBySeq.ContainsKey(seq)) throw new InvalidOperationException($"Anfrage {seq} ist noch offen.");
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { ServerId = _world.GetBlock(x, y, z) };
                _entries.Add(key, entry);
            }
            entry.Pending++;
            _pendingBySeq.Add(seq, key);
            _world.SetBlock(x, y, z, id);
        }

        /// <summary>Der Server meldet, was an einer Stelle steht.</summary>
        public void ApplyServer(int x, int y, int z, byte id)
        {
            if (!_world.IsInside(x, y, z)) return;
            if (_entries.TryGetValue(Key(x, y, z), out var entry)) entry.ServerId = id;
            else _world.SetBlock(x, y, z, id);
        }

        /// <summary>Der Server hat die Anfrage <paramref name="seq"/> beantwortet (angenommen oder abgelehnt).</summary>
        public void Resolve(ushort seq)
        {
            if (!_pendingBySeq.TryGetValue(seq, out var key)) return;
            _pendingBySeq.Remove(seq);
            var entry = _entries[key];
            if (--entry.Pending > 0) return;
            _entries.Remove(key);
            Unpack(key, out var x, out var y, out var z);
            _world.SetBlock(x, y, z, entry.ServerId);
        }

        private static long Key(int x, int y, int z) => ((long)x << 32) | ((long)y << 16) | (uint)z;

        private static void Unpack(long key, out int x, out int y, out int z)
        {
            x = (int)(key >> 32);
            y = (int)((key >> 16) & 0xFFFF);
            z = (int)(key & 0xFFFF);
        }
    }
}
