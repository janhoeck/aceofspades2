using System;
using System.Collections.Generic;

namespace AoS2.Shared.Voxels
{
    /// <summary>Eine Blockgruppe, die gerade fällt. Die Blöcke stehen währenddessen nicht in der Welt.</summary>
    public sealed class FallingGroup
    {
        internal FallingGroup(int id, List<GroupBlock> blocks)
        {
            Id = id;
            Blocks = blocks;
        }

        /// <summary>Fortlaufende Nummer (für die Darstellung im Client und im Netzwerk).</summary>
        public int Id { get; }

        /// <summary>Blöcke an ihrer Ausgangsposition (vor dem Fallen).</summary>
        public IReadOnlyList<GroupBlock> Blocks { get; }

        /// <summary>Bisher gefallene Strecke in Blöcken (≥ 0).</summary>
        public float Fallen { get; internal set; }

        public float Velocity { get; internal set; }

        /// <summary>Ganze Blöcke, um die die Gruppe schon sicher nach unten gefallen ist.</summary>
        internal int Settled;
    }

    /// <summary>
    /// Fallende Gruppen, wie die Darstellung sie sieht. Offline und auf dem Server rechnet <see cref="FallingBlocks"/>
    /// sie selbst, im Netzspiel kommen sie vom Server (<see cref="Net.Client.NetFallingGroups"/>).
    /// </summary>
    public interface IFallingGroups
    {
        IReadOnlyList<FallingGroup> Groups { get; }

        /// <summary>Eine Gruppe beginnt zu fallen (ihre Blöcke stehen nicht mehr in der Welt).</summary>
        event Action<FallingGroup> Started;

        /// <summary>Eine Gruppe ist aufgeschlagen und steht wieder in der Welt (um <c>landedOffset</c> Blöcke tiefer).</summary>
        event Action<FallingGroup, int> Landed;
    }

    /// <summary>
    /// Lässt schwebende Gruppen senkrecht fallen und setzt sie beim Aufprall wieder als Blöcke in die Welt.
    /// Ein Aufruf von <see cref="Tick"/> = ein Simulationsschritt; rechnet auf Client und Server gleich.
    /// </summary>
    public sealed class FallingBlocks : IFallingGroups
    {
        private readonly VoxelWorld _world;
        private readonly List<FallingGroup> _groups = new List<FallingGroup>();
        private int _nextId = 1;

        public FallingBlocks(VoxelWorld world, float gravity = 25f, float maxSpeed = 40f)
        {
            _world = world;
            Gravity = gravity;
            MaxSpeed = maxSpeed;
        }

        public float Gravity { get; }
        public float MaxSpeed { get; }

        /// <summary>Gruppen, die gerade fallen.</summary>
        public IReadOnlyList<FallingGroup> Groups => _groups;

        /// <summary>Eine Gruppe beginnt zu fallen (wird aus der Welt genommen).</summary>
        public event Action<FallingGroup> Started;

        /// <summary>Eine Gruppe ist aufgeschlagen und steht wieder in der Welt (um <c>landedOffset</c> Blöcke tiefer).</summary>
        public event Action<FallingGroup, int> Landed;

        /// <summary>
        /// Nach dem Entfernen von (x, y, z): schwebende Nachbargruppen suchen und fallen lassen.
        /// Liefert die Zahl der neuen fallenden Gruppen.
        /// </summary>
        public int CheckAfterRemoval(int x, int y, int z, int maxGroupSize = FloatingBlocks.DefaultMaxGroupSize)
        {
            var groups = FloatingBlocks.FindDetached(_world, x, y, z, maxGroupSize);
            foreach (var blocks in groups) StartFalling(blocks);
            return groups.Count;
        }

        /// <summary>Wie <see cref="CheckAfterRemoval"/> für viele entfernte Blöcke auf einmal (z. B. nach einer Explosion).</summary>
        public int CheckAfterRemovals(IReadOnlyList<BlockPos> removed, int maxGroupSize = FloatingBlocks.DefaultMaxGroupSize)
        {
            var groups = FloatingBlocks.FindDetached(_world, removed, maxGroupSize);
            foreach (var blocks in groups) StartFalling(blocks);
            return groups.Count;
        }

        /// <summary>Nimmt die Blöcke aus der Welt und lässt sie fallen.</summary>
        public FallingGroup StartFalling(List<GroupBlock> blocks)
        {
            var group = new FallingGroup(_nextId++, blocks);
            foreach (var b in blocks) _world.SetBlock(b.X, b.Y, b.Z, BlockRegistry.Air);
            _groups.Add(group);
            Started?.Invoke(group);
            return group;
        }

        public void Tick(float dt)
        {
            for (var i = _groups.Count - 1; i >= 0; i--)
            {
                var g = _groups[i];
                g.Velocity = Math.Min(g.Velocity + Gravity * dt, MaxSpeed);
                g.Fallen += g.Velocity * dt;

                // Schicht für Schicht: Ist die nächste Reihe frei, darf die Gruppe bis dorthin fallen.
                // Ist sie belegt, schlägt die Gruppe sofort auf (sonst würde sie kurz im Hindernis stecken).
                var target = DetMath.FloorToInt(g.Fallen);
                while (true)
                {
                    if (!CanMoveDown(g, g.Settled + 1))
                    {
                        Land(g, g.Settled);
                        _groups.RemoveAt(i);
                        break;
                    }
                    if (g.Settled >= target) break;
                    g.Settled++;
                }
            }
        }

        private bool CanMoveDown(FallingGroup g, int offset)
        {
            foreach (var b in g.Blocks)
            {
                var y = b.Y - offset;
                if (y < 0 || _world.IsSolid(b.X, y, b.Z)) return false;
            }
            return true;
        }

        private void Land(FallingGroup g, int offset)
        {
            foreach (var b in g.Blocks) _world.SetBlock(b.X, b.Y - offset, b.Z, b.Id);
            g.Fallen = offset;
            g.Velocity = 0;
            Landed?.Invoke(g, offset);
        }
    }
}
