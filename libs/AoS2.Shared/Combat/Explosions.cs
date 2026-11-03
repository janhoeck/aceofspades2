using System.Collections.Generic;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Combat
{
    /// <summary>
    /// Schaden einer Explosion an Blöcken und Spielern. Der Schaden nimmt vom Mittelpunkt bis zum Rand linear ab:
    /// <c>Schaden · (1 − Abstand / Radius)</c>. Spieler hinter Blöcken (keine Sichtlinie) bekommen nichts ab.
    /// </summary>
    public static class Explosions
    {
        /// <summary>
        /// Beschädigt alle Blöcke, deren Mitte im Radius liegt. Zerstörte Blöcke kommen in <paramref name="destroyed"/>
        /// (sie stehen noch in der Welt, entfernen muss der Aufrufer), beschädigte in <paramref name="damaged"/>.
        /// Die Reihenfolge ist fest (y, z, x), damit Client und Server, Test und Spiel dasselbe Ergebnis sehen.
        /// </summary>
        public static void DamageBlocks(VoxelWorld world, BlockDamage damage, Vec3 center, float radius, float maxDamage,
            List<BlockPos> destroyed, List<BlockPos> damaged)
        {
            if (radius <= 0 || maxDamage <= 0) return;
            int x0 = DetMath.FloorToInt(center.X - radius), x1 = DetMath.FloorToInt(center.X + radius);
            int y0 = DetMath.FloorToInt(center.Y - radius), y1 = DetMath.FloorToInt(center.Y + radius);
            int z0 = DetMath.FloorToInt(center.Z - radius), z1 = DetMath.FloorToInt(center.Z + radius);
            for (var y = y0; y <= y1; y++)
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                    {
                        if (!world.IsSolid(x, y, z)) continue;
                        var d = Vec3.Distance(center, new Vec3(x + 0.5f, y + 0.5f, z + 0.5f));
                        if (d >= radius) continue;
                        var amount = maxDamage * (1 - d / radius);
                        var pos = new BlockPos(x, y, z);
                        if (damage.Add(x, y, z, amount)) destroyed.Add(pos);
                        else if (damage.Get(x, y, z) > 0) damaged.Add(pos);
                    }
        }

        /// <summary>
        /// Schaden an einem Spieler (Kollisionsquader ab <paramref name="feet"/>). Abstand = vom Mittelpunkt zum nächsten Punkt
        /// des Quaders; Sichtlinie zu Füßen, Brust oder Kopf genügt.
        /// </summary>
        public static float PlayerDamage(VoxelWorld world, Vec3 center, float radius, float maxDamage, Vec3 feet, float halfWidth, float height)
        {
            var min = new Vec3(feet.X - halfWidth, feet.Y, feet.Z - halfWidth);
            var max = new Vec3(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth);
            var distance = Vec3.Distance(center, Geometry.ClosestPoint(min, max, center));
            if (distance >= radius) return 0;
            if (!Visible(world, center, feet + new Vec3(0, height * 0.5f, 0)) &&
                !Visible(world, center, feet + new Vec3(0, height - 0.2f, 0)) &&
                !Visible(world, center, feet + new Vec3(0, 0.2f, 0)))
                return 0;
            return maxDamage * (1 - distance / radius);
        }

        /// <summary>Kein fester Block zwischen den beiden Punkten?</summary>
        public static bool Visible(VoxelWorld world, Vec3 from, Vec3 to)
        {
            var delta = to - from;
            var length = delta.Length;
            if (length < 1e-4f) return true;
            return !VoxelRaycast.Cast(world, from, delta * (1 / length), length, out var hit) || hit.Distance >= length - 0.01f;
        }
    }
}
