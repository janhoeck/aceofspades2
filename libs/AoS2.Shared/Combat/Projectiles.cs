using AoS2.Shared.Voxels;

namespace AoS2.Shared.Combat
{
    /// <summary>Ein fliegendes Geschoss (Granate oder Rakete).</summary>
    public struct Projectile
    {
        public int Id;

        /// <summary>Platz der Waffe, die es abgefeuert hat (Werte aus weapons.json).</summary>
        public byte Weapon;

        public byte Owner;
        public Vec3 Position;
        public Vec3 Velocity;

        /// <summary>Ticks seit dem Abschuss.</summary>
        public int Age;
    }

    /// <summary>
    /// Flug eines Geschosses, ein Aufruf = ein Tick. Der Server rechnet verbindlich; der Client rechnet dieselbe Bahn nur
    /// zur Anzeige nach (Start und Explosion kommen vom Server). Gegen die Welt wird der Weg jedes Ticks als Strahl geprüft,
    /// so fliegt auch eine schnelle Rakete nicht durch dünne Wände.
    /// <list type="bullet">
    /// <item>Rakete: fliegt gerade (ohne Schwerkraft, wenn <c>gravity</c> 0 ist) und explodiert am ersten Block.</item>
    /// <item>Granate: fällt, prallt an Blöcken ab (<c>bounce</c>: Anteil der Geschwindigkeit senkrecht zur Fläche,
    ///       <c>friction</c>: Anteil entlang der Fläche) und explodiert nach der Zündzeit.</item>
    /// </list>
    /// </summary>
    public static class ProjectileSim
    {
        /// <summary>Abstand zur getroffenen Fläche nach einem Aufprall (damit das Geschoss nicht im Block steckt).</summary>
        private const float SurfaceGap = 0.02f;

        /// <summary>Langsamer als das (Blöcke/s) bleibt eine Granate liegen.</summary>
        private const float RestSpeed = 0.5f;

        /// <summary>Ein Tick. <c>true</c> = explodiert jetzt bei <see cref="Projectile.Position"/>.</summary>
        public static bool Step(ref Projectile p, WeaponSettings weapon, VoxelWorld world, float dt)
        {
            p.Age++;
            p.Velocity.Y -= weapon.Gravity * dt;
            var move = p.Velocity * dt;
            var length = move.Length;
            if (length > 0)
            {
                var dir = move * (1 / length);
                if (VoxelRaycast.Cast(world, p.Position, dir, length, out var hit))
                {
                    var travel = hit.Distance - SurfaceGap;
                    p.Position = p.Position + dir * (travel > 0 ? travel : 0);
                    if (weapon.Kind == WeaponKind.Rocket) return true;
                    Bounce(ref p, weapon, hit);
                }
                else
                {
                    p.Position = p.Position + move;
                }
            }

            var fuse = weapon.Kind == WeaponKind.Grenade && weapon.FuseTicks > 0 ? weapon.FuseTicks : weapon.LifetimeTicks;
            return p.Age >= fuse;
        }

        private static void Bounce(ref Projectile p, WeaponSettings weapon, VoxelHit hit)
        {
            var v = p.Velocity;
            if (hit.NormalX == 0 && hit.NormalY == 0 && hit.NormalZ == 0)
            {
                p.Velocity = Vec3.Zero; // steckt im Block: liegen bleiben
                return;
            }
            float b = weapon.Bounce, f = weapon.Friction;
            if (hit.NormalX != 0) v = new Vec3(-v.X * b, v.Y * f, v.Z * f);
            else if (hit.NormalY != 0) v = new Vec3(v.X * f, -v.Y * b, v.Z * f);
            else v = new Vec3(v.X * f, v.Y * f, -v.Z * b);
            // Auf dem Boden kleine Hüpfer weglassen, sonst zittert eine liegende Granate.
            if (hit.NormalY > 0 && v.Y < weapon.Gravity * 0.1f) v.Y = 0;
            if (v.Length < RestSpeed) v = Vec3.Zero;
            p.Velocity = v;
        }
    }
}
