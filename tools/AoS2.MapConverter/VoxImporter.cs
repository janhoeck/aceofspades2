using AoS2.Shared.Voxels;

namespace AoS2.MapConverter;

/// <summary>Fehler beim Umwandeln, mit einer Meldung, die man dem Map-Bauer direkt zeigen kann.</summary>
public sealed class ConversionException(string message) : Exception(message);

/// <summary>
/// Setzt die Modelle einer .vox-Datei zu einer Blockwelt zusammen.
/// Achsen: MagicaVoxel hat z oben, das Spiel y oben. Darum wird (x, y, z) zu (x, z, y).
/// Die Welt beginnt an der kleinsten belegten Stelle aller Modelle (wird auf 0/0/0 verschoben);
/// ihre Größe ist die Ausdehnung aller Voxel, falls keine feste Größe vorgegeben ist.
/// </summary>
public static class VoxImporter
{
    public static VoxelWorld Convert(VoxFile vox, ColorMapping mapping, (int X, int Y, int Z)? size = null)
    {
        var instances = vox.GetInstances();
        var placed = new List<(int X, int Y, int Z, byte Color)>();
        foreach (var instance in instances)
        {
            var m = instance.Model;
            // MagicaVoxel dreht und verschiebt Modelle um ihre Mitte.
            int px = m.SizeX / 2, py = m.SizeY / 2, pz = m.SizeZ / 2;
            foreach (var v in m.Voxels)
            {
                var (x, y, z) = instance.Transform.Apply(v.X - px, v.Y - py, v.Z - pz);
                placed.Add((x, z, y, v.ColorIndex)); // z oben → y oben
            }
        }
        if (placed.Count == 0) throw new ConversionException("Die .vox-Datei enthält keine sichtbaren Voxel.");

        // Farben ohne Zuordnung sammeln und alle auf einmal melden
        var missing = new SortedDictionary<uint, int>();
        foreach (var p in placed)
        {
            var rgb = vox.Palette[p.Color];
            if (!mapping.TryGet(rgb, out _)) missing[rgb] = missing.GetValueOrDefault(rgb) + 1;
        }
        if (missing.Count > 0)
        {
            var lines = missing.Select(kv => $"  {ColorMapping.FormatColor(kv.Key)}  ({kv.Value} Voxel)");
            throw new ConversionException("Diese Farben haben keinen Blocktyp in der Zuordnungsdatei:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }

        int minX = placed.Min(p => p.X), minY = placed.Min(p => p.Y), minZ = placed.Min(p => p.Z);
        int extentX = placed.Max(p => p.X) - minX + 1, extentY = placed.Max(p => p.Y) - minY + 1, extentZ = placed.Max(p => p.Z) - minZ + 1;
        var (sx, sy, sz) = size ?? (extentX, extentY, extentZ);
        if (extentX > sx || extentY > sy || extentZ > sz)
            throw new ConversionException($"Die Voxel belegen {extentX} × {extentY} × {extentZ} Blöcke (x × Höhe × z), mehr als die vorgegebene Größe {sx} × {sy} × {sz}.");

        var world = new VoxelWorld(sx, sy, sz);
        foreach (var p in placed)
        {
            mapping.TryGet(vox.Palette[p.Color], out var type);
            world.SetBlock(p.X - minX, p.Y - minY, p.Z - minZ, type.Id);
        }
        return world;
    }
}
