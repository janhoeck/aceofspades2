using AoS2.Shared.Movement;

namespace AoS2.Shared.Voxels
{
    public enum BlockEditResult
    {
        Ok,
        OutOfReach,
        OutsideWorld,
        Indestructible,
        NotBuildable,
        Occupied,
        BlockedByPlayer,
        NothingThere,
    }

    /// <summary>
    /// Regeln fürs Abbauen und Bauen. Heute prüft sie der Client allein; ab M4 prüft der Server dieselben Regeln.
    /// </summary>
    public static class BlockEditing
    {
        /// <summary>Maximale Entfernung vom Auge bis zum Block (in Blöcken).</summary>
        public const float Reach = 5f;

        /// <summary>Mindestabstand zwischen zwei Aktionen in Ticks (12 Ticks = 0,2 s bei 60 Ticks/s).</summary>
        public const int CooldownTicks = 12;

        public static BlockEditResult CanRemove(VoxelWorld world, BlockRegistry registry, int x, int y, int z, float distance)
        {
            if (distance > Reach) return BlockEditResult.OutOfReach;
            if (!world.IsInside(x, y, z)) return BlockEditResult.OutsideWorld;
            var id = world.GetBlock(x, y, z);
            if (id == BlockRegistry.Air) return BlockEditResult.NothingThere;
            if (registry.IsIndestructible(id)) return BlockEditResult.Indestructible;
            return BlockEditResult.Ok;
        }

        /// <param name="player">Zustand des bauenden Spielers: In den eigenen Quader darf man nicht bauen.</param>
        public static BlockEditResult CanPlace(VoxelWorld world, BlockRegistry registry, int x, int y, int z, byte id, float distance,
            PlayerState player, MovementSettings settings)
        {
            if (distance > Reach) return BlockEditResult.OutOfReach;
            if (!world.IsInside(x, y, z)) return BlockEditResult.OutsideWorld;
            var type = registry.Get(id);
            if (type == null || !type.Buildable) return BlockEditResult.NotBuildable;
            if (world.IsSolid(x, y, z)) return BlockEditResult.Occupied;
            if (PlayerMovement.OverlapsBlock(player.Position, settings.HalfWidth, settings.Height(player.Crouching), x, y, z))
                return BlockEditResult.BlockedByPlayer;
            return BlockEditResult.Ok;
        }
    }
}
