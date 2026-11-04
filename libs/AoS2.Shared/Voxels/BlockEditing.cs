using AoS2.Shared.Maps;
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

        /// <summary>Server: Die Pause seit der letzten Aktion war zu kurz.</summary>
        TooFast,

        /// <summary>Server: Vom Auge aus trifft der Blick nicht genau diesen Block (verdeckt oder geschummelt).</summary>
        NoLineOfSight,

        /// <summary>Server: Tote können weder abbauen noch bauen.</summary>
        Dead,

        /// <summary>Der Block liegt in einer Spawnzone oder außerhalb der Kampfzone (<see cref="MapZones.CanEditBlock"/>).</summary>
        ProtectedZone,

        /// <summary>Server: Das Match ist vorbei, bis zum Neustart ändert sich nichts.</summary>
        MatchOver,

        /// <summary>Der Blockvorrat ist leer (abbauen füllt ihn wieder auf, der Respawn ganz).</summary>
        NoBlocks,
    }

    /// <summary>
    /// Regeln fürs Abbauen und Bauen. Der Client prüft sie vorab (damit er die Änderung sofort zeigen kann),
    /// der Server prüft dieselben Regeln noch einmal und hat das letzte Wort.
    /// </summary>
    public static class BlockEditing
    {
        /// <summary>Maximale Entfernung vom Auge bis zum Block (in Blöcken).</summary>
        public const float Reach = 5f;

        /// <summary>
        /// Mindestabstand zwischen zwei Aktionen in Ticks (12 Ticks = 0,2 s bei 60 Ticks/s), wenn die Klasse nichts anderes sagt
        /// (<c>editSeconds</c> in classes.json, <see cref="Combat.PlayerClass.EditCooldownTicks"/>).
        /// </summary>
        public const int CooldownTicks = 12;

        /// <param name="zones">Zonen der Map; <c>null</c> = keine (überall erlaubt).</param>
        public static BlockEditResult CanRemove(VoxelWorld world, BlockRegistry registry, int x, int y, int z, float distance, MapZones zones = null)
        {
            if (distance > Reach) return BlockEditResult.OutOfReach;
            if (!world.IsInside(x, y, z)) return BlockEditResult.OutsideWorld;
            var id = world.GetBlock(x, y, z);
            if (id == BlockRegistry.Air) return BlockEditResult.NothingThere;
            if (registry.IsIndestructible(id)) return BlockEditResult.Indestructible;
            if (zones != null && !zones.CanEditBlock(x, y, z)) return BlockEditResult.ProtectedZone;
            return BlockEditResult.Ok;
        }

        /// <param name="player">Zustand des bauenden Spielers: In den eigenen Quader darf man nicht bauen.</param>
        /// <param name="zones">Zonen der Map; <c>null</c> = keine (überall erlaubt).</param>
        public static BlockEditResult CanPlace(VoxelWorld world, BlockRegistry registry, int x, int y, int z, byte id, float distance,
            PlayerState player, MovementSettings settings, MapZones zones = null)
        {
            if (distance > Reach) return BlockEditResult.OutOfReach;
            if (!world.IsInside(x, y, z)) return BlockEditResult.OutsideWorld;
            var type = registry.Get(id);
            if (type == null || !type.Buildable) return BlockEditResult.NotBuildable;
            if (world.IsSolid(x, y, z)) return BlockEditResult.Occupied;
            if (zones != null && !zones.CanEditBlock(x, y, z)) return BlockEditResult.ProtectedZone;
            if (PlayerMovement.OverlapsBlock(player.Position, settings.HalfWidth, settings.Height(player.Crouching), x, y, z))
                return BlockEditResult.BlockedByPlayer;
            return BlockEditResult.Ok;
        }
    }
}
