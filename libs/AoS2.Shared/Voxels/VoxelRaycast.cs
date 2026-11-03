namespace AoS2.Shared.Voxels
{
    /// <summary>Ergebnis eines Strahls gegen die Blockwelt.</summary>
    public struct VoxelHit
    {
        /// <summary>Getroffener Block.</summary>
        public int X, Y, Z;

        /// <summary>Normale der getroffenen Seite (zeigt zum Strahl-Ursprung). 0/0/0, wenn der Ursprung im Block liegt.</summary>
        public int NormalX, NormalY, NormalZ;

        /// <summary>Strecke vom Ursprung bis zum Eintritt in den Block.</summary>
        public float Distance;

        /// <summary>Stelle, an der ein Block auf die getroffene Seite gebaut würde.</summary>
        public int PlaceX => X + NormalX;
        public int PlaceY => Y + NormalY;
        public int PlaceZ => Z + NormalZ;
    }

    /// <summary>
    /// Strahl durch das Blockgitter (Verfahren nach Amanatides &amp; Woo): springt von Blockgrenze zu Blockgrenze
    /// und prüft so jeden Block, den der Strahl berührt, genau einmal.
    /// </summary>
    public static class VoxelRaycast
    {
        /// <param name="direction">Richtung, muss normiert sein.</param>
        public static bool Cast(VoxelWorld world, Vec3 origin, Vec3 direction, float maxDistance, out VoxelHit hit)
        {
            hit = default;
            int x = DetMath.FloorToInt(origin.X), y = DetMath.FloorToInt(origin.Y), z = DetMath.FloorToInt(origin.Z);
            int stepX = Sign(direction.X), stepY = Sign(direction.Y), stepZ = Sign(direction.Z);

            // Strecke entlang des Strahls bis zur nächsten Grenze je Achse, und Strecke für einen ganzen Block
            float tMaxX = Boundary(origin.X, direction.X, x), tMaxY = Boundary(origin.Y, direction.Y, y), tMaxZ = Boundary(origin.Z, direction.Z, z);
            float tDeltaX = Delta(direction.X), tDeltaY = Delta(direction.Y), tDeltaZ = Delta(direction.Z);

            int nx = 0, ny = 0, nz = 0;
            var t = 0f;
            while (t <= maxDistance)
            {
                if (world.IsSolid(x, y, z))
                {
                    hit.X = x;
                    hit.Y = y;
                    hit.Z = z;
                    hit.NormalX = nx;
                    hit.NormalY = ny;
                    hit.NormalZ = nz;
                    hit.Distance = t;
                    return true;
                }

                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    nx = -stepX; ny = 0; nz = 0;
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                    nx = 0; ny = -stepY; nz = 0;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    nx = 0; ny = 0; nz = -stepZ;
                }
            }
            return false;
        }

        private static int Sign(float v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        private static float Boundary(float origin, float dir, int cell)
        {
            if (dir > 0) return (cell + 1 - origin) / dir;
            if (dir < 0) return (origin - cell) / -dir;
            return float.PositiveInfinity;
        }

        private static float Delta(float dir) => dir == 0 ? float.PositiveInfinity : 1f / (dir < 0 ? -dir : dir);
    }
}
