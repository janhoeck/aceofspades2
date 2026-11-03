using AoS2.Shared.Maps;
using AoS2.Shared.Voxels;

namespace AoS2.MapConverter;

/// <summary>Prüft Metadaten gegen die fertige Welt, damit Fehler beim Umwandeln auffallen und nicht erst im Spiel.</summary>
public static class MapChecks
{
    public static void Validate(VoxelWorld world, MapMetadata metadata)
    {
        var problems = new List<string>();
        if (metadata.CombatZone is { } combat && !InsideWorld(world, combat)) problems.Add("Die Kampfzone ragt aus der Map heraus.");

        foreach (var (team, spawn) in metadata.Teams)
        {
            var name = MapMetadata.TeamName(team);
            if (!InsideWorld(world, spawn.Zone)) problems.Add($"Die Spawnzone von {name} ragt aus der Map heraus.");
            if (spawn.Points.Count == 0) problems.Add($"Team {name} hat keine Spawnpunkte.");
            foreach (var p in spawn.Points)
            {
                int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
                if (!world.IsInside(x, y, z)) problems.Add($"Spawnpunkt {p} von {name} liegt außerhalb der Map.");
                else if (world.IsSolid(x, y, z) || world.IsSolid(x, y + 1, z)) problems.Add($"Spawnpunkt {p} von {name} steckt in Blöcken.");
                else if (!spawn.Zone.Contains(x, y, z)) problems.Add($"Spawnpunkt {p} von {name} liegt nicht in der Spawnzone.");
            }
        }

        if (problems.Count > 0) throw new ConversionException("Die Metadaten passen nicht zur Map:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
    }

    private static bool InsideWorld(VoxelWorld world, BlockBox box) =>
        box.MinX >= 0 && box.MinY >= 0 && box.MinZ >= 0 && box.MaxX <= world.SizeX && box.MaxY <= world.SizeY && box.MaxZ <= world.SizeZ;
}
