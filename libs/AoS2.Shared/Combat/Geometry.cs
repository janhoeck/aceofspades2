namespace AoS2.Shared.Combat
{
    /// <summary>
    /// Gedrehter Quader (oriented bounding box): Mittelpunkt, drei zueinander senkrechte Achsen (Länge 1) und die halbe
    /// Kantenlänge entlang jeder Achse.
    /// </summary>
    public struct Obb
    {
        public Vec3 Center;
        public Vec3 AxisX, AxisY, AxisZ;
        public Vec3 Half;

        /// <summary>Ecke <paramref name="i"/> (0 … 7, Bit 0/1/2 = Vorzeichen entlang x/y/z).</summary>
        public Vec3 Corner(int i) =>
            Center + AxisX * ((i & 1) != 0 ? Half.X : -Half.X) + AxisY * ((i & 2) != 0 ? Half.Y : -Half.Y) + AxisZ * ((i & 4) != 0 ? Half.Z : -Half.Z);
    }

    /// <summary>Strahl-Tests für Treffer. Richtungen müssen Länge 1 haben; Entfernungen sind Strecken entlang des Strahls.</summary>
    public static class Geometry
    {
        private const float Parallel = 1e-8f;

        /// <summary>Berührt der Strahl die Kugel innerhalb von <paramref name="maxDistance"/>? (Grobprüfung vor den Boxen.)</summary>
        public static bool RaySphere(Vec3 origin, Vec3 direction, Vec3 center, float radius, float maxDistance)
        {
            var toCenter = center - origin;
            var along = Vec3.Dot(toCenter, direction);
            var distSq = toCenter.LengthSquared - along * along; // Abstand² der Kugelmitte vom Strahl
            var r2 = radius * radius;
            if (distSq > r2) return false;
            if (toCenter.LengthSquared <= r2) return true; // Ursprung in der Kugel
            if (along < 0) return false; // Kugel liegt hinter dem Ursprung
            return along - DetMath.Sqrt(r2 - distSq) <= maxDistance;
        }

        /// <summary>
        /// Strahl gegen gedrehten Quader: Der Strahl wird in die Achsen des Quaders umgerechnet, dann ist es ein
        /// achsenparalleler Test („Slab“-Verfahren). <paramref name="distance"/> = Eintritt (0, wenn der Ursprung drinliegt).
        /// </summary>
        public static bool RayObb(Vec3 origin, Vec3 direction, Obb box, float maxDistance, out float distance)
        {
            var d = origin - box.Center;
            var o = new Vec3(Vec3.Dot(d, box.AxisX), Vec3.Dot(d, box.AxisY), Vec3.Dot(d, box.AxisZ));
            var dir = new Vec3(Vec3.Dot(direction, box.AxisX), Vec3.Dot(direction, box.AxisY), Vec3.Dot(direction, box.AxisZ));
            return RayAabb(o, dir, -box.Half, box.Half, maxDistance, out distance);
        }

        /// <summary>Strahl gegen achsenparallelen Quader [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public static bool RayAabb(Vec3 origin, Vec3 direction, Vec3 min, Vec3 max, float maxDistance, out float distance)
        {
            float enter = 0, exit = maxDistance;
            distance = 0;
            if (!Slab(origin.X, direction.X, min.X, max.X, ref enter, ref exit)) return false;
            if (!Slab(origin.Y, direction.Y, min.Y, max.Y, ref enter, ref exit)) return false;
            if (!Slab(origin.Z, direction.Z, min.Z, max.Z, ref enter, ref exit)) return false;
            distance = enter;
            return true;
        }

        private static bool Slab(float origin, float dir, float min, float max, ref float enter, ref float exit)
        {
            if (dir > -Parallel && dir < Parallel) return origin >= min && origin <= max;
            var t1 = (min - origin) / dir;
            var t2 = (max - origin) / dir;
            if (t1 > t2)
            {
                var tmp = t1;
                t1 = t2;
                t2 = tmp;
            }
            if (t1 > enter) enter = t1;
            if (t2 < exit) exit = t2;
            return enter <= exit;
        }

        /// <summary>Nächster Punkt eines achsenparallelen Quaders zu <paramref name="p"/> (liegt p drin, ist es p selbst).</summary>
        public static Vec3 ClosestPoint(Vec3 min, Vec3 max, Vec3 p) =>
            new Vec3(DetMath.Clamp(p.X, min.X, max.X), DetMath.Clamp(p.Y, min.Y, max.Y), DetMath.Clamp(p.Z, min.Z, max.Z));
    }
}
