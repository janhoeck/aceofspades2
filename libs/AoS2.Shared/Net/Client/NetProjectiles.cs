using System.Collections.Generic;
using AoS2.Shared.Combat;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net.Client
{
    /// <summary>Ein Geschoss, wie der Client es zeigt.</summary>
    public sealed class NetProjectile
    {
        internal Projectile Sim;
        internal Vec3 Previous;
        internal uint SimTick;
        internal bool Stopped;
        internal int StoppedTicks;

        public int Id => Sim.Id;
        public byte Weapon => Sim.Weapon;
        public byte Owner => Sim.Owner;

        /// <summary>Position zum Darstellungszeitpunkt (nach <see cref="NetProjectiles.UpdateRender"/>).</summary>
        public Vec3 Position { get; internal set; }

        /// <summary>Flugrichtung (für die Ausrichtung des Modells).</summary>
        public Vec3 Velocity => Sim.Velocity;
    }

    /// <summary>
    /// Geschosse im Netzspiel. Der Server meldet Start (Ort, Geschwindigkeit, Tick) und Explosion; dazwischen rechnet der
    /// Client die Bahn mit demselben Code (<see cref="ProjectileSim"/>) selbst nach. So braucht ein Geschoss keinen Platz im
    /// Snapshot und fliegt trotzdem flüssig. Kommt der Client vor der Meldung des Servers an einem Hindernis an, bleibt das
    /// Geschoss dort stehen, bis die Explosion gemeldet wird.
    /// </summary>
    public sealed class NetProjectiles
    {
        /// <summary>So lange wartet ein angehaltenes Geschoss auf die Explosion vom Server, dann verschwindet es.</summary>
        private const int MaxStoppedTicks = 120;

        /// <summary>Höchstens so viele Ticks wird nach dem Start auf einmal nachgerechnet (z. B. beim Beitritt).</summary>
        private const int MaxCatchUpTicks = 600;

        private readonly VoxelWorld _world;
        private readonly CombatSettings _combat;
        private readonly float _dt;
        private readonly Dictionary<int, NetProjectile> _all = new Dictionary<int, NetProjectile>();
        private readonly List<int> _remove = new List<int>();

        public NetProjectiles(VoxelWorld world, CombatSettings combat, float dt)
        {
            _world = world;
            _combat = combat;
            _dt = dt;
        }

        public IReadOnlyDictionary<int, NetProjectile> All => _all;

        public void Spawn(int id, byte weapon, byte owner, uint tick, Vec3 position, Vec3 velocity)
        {
            if (_combat?.Weapon(weapon) == null) return;
            _all[id] = new NetProjectile
            {
                Sim = new Projectile { Id = id, Weapon = weapon, Owner = owner, Position = position, Velocity = velocity },
                Previous = position,
                SimTick = tick,
                Position = position,
            };
        }

        public void Explode(int id) => _all.Remove(id);

        /// <summary>Rechnet alle Geschosse bis zum Server-Tick <paramref name="serverTick"/> und setzt die Anzeigeposition.</summary>
        public void UpdateRender(double serverTick)
        {
            _remove.Clear();
            foreach (var p in _all.Values)
            {
                var weapon = _combat.Weapon(p.Sim.Weapon);
                var steps = 0;
                while (p.SimTick + 1 <= serverTick && steps++ < MaxCatchUpTicks)
                {
                    p.SimTick++;
                    p.Previous = p.Sim.Position;
                    if (p.Stopped)
                    {
                        if (++p.StoppedTicks > MaxStoppedTicks) _remove.Add(p.Id);
                        continue;
                    }
                    if (ProjectileSim.Step(ref p.Sim, weapon, _world, _dt)) p.Stopped = true;
                }
                var t = (float)(serverTick - p.SimTick); // einen Tick zurück, dafür flüssig zwischen zwei gerechneten Ticks
                p.Position = p.Stopped ? p.Sim.Position : Vec3.Lerp(p.Previous, p.Sim.Position, t < 0 ? 0 : t > 1 ? 1 : t);
            }
            foreach (var id in _remove) _all.Remove(id);
        }
    }
}
