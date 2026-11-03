using System.Collections.Generic;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Maps
{
    /// <summary>Wo ein Spieler erscheint: an den Spawnpunkten seines Teams, reihum.</summary>
    public static class Spawns
    {
        /// <summary>
        /// Spawnpunkt Nummer <paramref name="index"/> (reihum über alle Teams). Ohne Spawnpunkte die Mitte der Map
        /// auf dem höchsten Block.
        /// </summary>
        public static Vec3 Pick(VoxelWorld world, MapMetadata metadata, int index)
        {
            var points = new List<Vec3>(metadata.AllSpawnPoints());
            if (points.Count > 0) return points[Wrap(index, points.Count)];
            return Center(world);
        }

        /// <summary>
        /// Spawnpunkt Nummer <paramref name="index"/> des Teams (reihum). Hat das Team keine Spawnpunkte, alle Spawnpunkte
        /// der Map; ohne Team die Mitte der Map (dort schauen Zuschauer zu, bis sie ein Team gewählt haben).
        /// </summary>
        public static Vec3 Pick(VoxelWorld world, MapMetadata metadata, Team team, int index)
        {
            if (team == Team.None) return Center(world);
            if (metadata.Teams.TryGetValue(team, out var spawn) && spawn.Points.Count > 0)
                return spawn.Points[Wrap(index, spawn.Points.Count)];
            return Pick(world, metadata, index);
        }

        /// <summary>Mitte der Map auf dem höchsten Block.</summary>
        public static Vec3 Center(VoxelWorld world)
        {
            int x = world.SizeX / 2, z = world.SizeZ / 2, y = world.SizeY;
            while (y > 0 && !world.IsSolid(x, y - 1, z)) y--;
            return new Vec3(x + 0.5f, y, z + 0.5f);
        }

        private static int Wrap(int index, int count) => ((index % count) + count) % count;
    }
}
