using System.Collections.Generic;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Maps
{
    /// <summary>Wo ein Spieler erscheint (bis es in M6 Teams gibt: reihum über alle Spawnpunkte).</summary>
    public static class Spawns
    {
        /// <summary>
        /// Spawnpunkt Nummer <paramref name="index"/> (reihum über alle Teams). Ohne Spawnpunkte die Mitte der Map
        /// auf dem höchsten Block.
        /// </summary>
        public static Vec3 Pick(VoxelWorld world, MapMetadata metadata, int index)
        {
            var points = new List<Vec3>(metadata.AllSpawnPoints());
            if (points.Count > 0) return points[((index % points.Count) + points.Count) % points.Count];

            int x = world.SizeX / 2, z = world.SizeZ / 2, y = world.SizeY;
            while (y > 0 && !world.IsSolid(x, y - 1, z)) y--;
            return new Vec3(x + 0.5f, y, z + 0.5f);
        }
    }
}
