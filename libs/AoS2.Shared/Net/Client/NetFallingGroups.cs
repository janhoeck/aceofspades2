using System;
using System.Collections.Generic;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net.Client
{
    /// <summary>
    /// Fallende Blockgruppen im Netzspiel. Der Server erkennt und simuliert sie; der Client bekommt
    /// Start und Aufprall als zuverlässige Ereignisse und die Fallhöhe dazwischen per Snapshot,
    /// zwischen denen er interpoliert.
    /// </summary>
    public sealed class NetFallingGroups : IFallingGroups
    {
        /// <summary>
        /// Fallende Gruppen werden nur knapp verzögert gezeigt (andere Spieler: <see cref="ClientSession.InterpolationDelayTicks"/>):
        /// Beim Aufprall stehen die Blöcke sofort in der Welt, und je weiter die Anzeige hinterherhinkt, desto mehr springt sie dann.
        /// </summary>
        public const double DelayTicks = 2;

        private readonly PredictedBlocks _blocks;
        private readonly List<FallingGroup> _groups = new List<FallingGroup>();
        private readonly Dictionary<int, InterpolationBuffer<float>> _samples = new Dictionary<int, InterpolationBuffer<float>>();
        private static readonly Func<float, float, float, float> Lerp = (a, b, t) => a + (b - a) * t;

        public NetFallingGroups(PredictedBlocks blocks)
        {
            _blocks = blocks;
        }

        public IReadOnlyList<FallingGroup> Groups => _groups;

        public event Action<FallingGroup> Started;
        public event Action<FallingGroup, int> Landed;

        public void Start(int groupId, List<GroupBlock> blocks, float fallen, float velocity, uint serverTick)
        {
            if (Find(groupId) != null) return;
            var group = new FallingGroup(groupId, blocks) { Fallen = fallen, Velocity = velocity };
            foreach (var b in blocks) _blocks.ApplyServer(b.X, b.Y, b.Z, BlockRegistry.Air);
            _groups.Add(group);
            var buffer = new InterpolationBuffer<float>();
            buffer.Add(serverTick, fallen);
            _samples.Add(groupId, buffer);
            Started?.Invoke(group);
        }

        public void Land(int groupId, int offset)
        {
            var group = Find(groupId);
            if (group == null) return;
            foreach (var b in group.Blocks) _blocks.ApplyServer(b.X, b.Y - offset, b.Z, b.Id);
            group.Fallen = offset;
            group.Velocity = 0;
            _groups.Remove(group);
            _samples.Remove(groupId);
            Landed?.Invoke(group, offset);
        }

        /// <summary>
        /// Alle Gruppen verwerfen (Neustart eines Matches). Die Welt setzt der Server mit eigenen Blockänderungen zurück; die
        /// Anzeige erfährt es wie bei einer Landung (ohne dass hier Blöcke gesetzt werden).
        /// </summary>
        public void Clear()
        {
            var groups = _groups.ToArray();
            _groups.Clear();
            _samples.Clear();
            foreach (var g in groups) Landed?.Invoke(g, 0);
        }

        public void OnSnapshot(uint serverTick, List<FallingSnapshot> falling)
        {
            foreach (var f in falling)
                if (_samples.TryGetValue(f.GroupId, out var buffer))
                    buffer.Add(serverTick, f.Fallen);
        }

        /// <summary>Setzt <see cref="FallingGroup.Fallen"/> jeder Gruppe für den Darstellungszeitpunkt.</summary>
        public void UpdateRender(double serverTick)
        {
            foreach (var g in _groups)
                if (_samples[g.Id].SampleExtrapolated(serverTick - DelayTicks, Lerp, 4, out var fallen))
                    g.Fallen = Math.Max(0f, fallen);
        }

        private FallingGroup Find(int id)
        {
            foreach (var g in _groups)
                if (g.Id == id) return g;
            return null;
        }
    }
}
