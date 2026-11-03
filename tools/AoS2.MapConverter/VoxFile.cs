using System.Text;

namespace AoS2.MapConverter;

/// <summary>Ein Voxel eines MagicaVoxel-Modells: Position im Modell und Farbindex (1–255).</summary>
public readonly record struct Voxel(int X, int Y, int Z, byte ColorIndex);

/// <summary>Ein Modell (Würfel aus Voxeln) aus dem SIZE/XYZI-Paar einer .vox-Datei.</summary>
public sealed record VoxModel(int SizeX, int SizeY, int SizeZ, IReadOnlyList<Voxel> Voxels);

/// <summary>
/// Lage eines Modells in der Szene: <c>Welt = Drehung · (Voxel − Mitte) + Verschiebung</c>.
/// Drehung als 3×3-Matrix aus −1/0/1 (zeilenweise), alles in MagicaVoxel-Achsen (z = oben).
/// </summary>
public readonly record struct VoxTransform(int[] Rotation, int TX, int TY, int TZ)
{
    public static VoxTransform Identity => new(new[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, 0, 0, 0);

    public (int X, int Y, int Z) Apply(int x, int y, int z) =>
        (Rotation[0] * x + Rotation[1] * y + Rotation[2] * z + TX,
         Rotation[3] * x + Rotation[4] * y + Rotation[5] * z + TY,
         Rotation[6] * x + Rotation[7] * y + Rotation[8] * z + TZ);

    /// <summary>Erst <paramref name="child"/>, dann diese Transformation.</summary>
    public VoxTransform Then(VoxTransform child)
    {
        var r = new int[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                r[i * 3 + j] = Rotation[i * 3] * child.Rotation[j] + Rotation[i * 3 + 1] * child.Rotation[3 + j] + Rotation[i * 3 + 2] * child.Rotation[6 + j];
        var (tx, ty, tz) = Apply(child.TX, child.TY, child.TZ);
        return new VoxTransform(r, tx, ty, tz);
    }
}

/// <summary>Ein Modell an seiner Stelle in der Szene.</summary>
public sealed record VoxInstance(VoxModel Model, VoxTransform Transform);

/// <summary>
/// Liest MagicaVoxel-Dateien (.vox, Version 150 und 200): Modelle, Palette und den Szenengraphen
/// (nTRN/nGRP/nSHP), damit eine Welt aus mehreren, verschobenen oder gedrehten Modellen bestehen kann.
/// Ausgeblendete Knoten und Ebenen werden übersprungen. Formatbeschreibung:
/// https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox.txt
/// </summary>
public sealed class VoxFile
{
    private readonly Dictionary<int, SceneNode> _nodes = new();
    private readonly HashSet<int> _hiddenLayers = new();

    public List<VoxModel> Models { get; } = new();

    /// <summary>Palette: Index 1–255 → Farbe als 0xRRGGBB (Index 0 ist leer).</summary>
    public uint[] Palette { get; } = DefaultPalette();

    public static VoxFile Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "VOX ") throw new InvalidDataException("Keine MagicaVoxel-Datei (Kennung \"VOX \" fehlt).");
            reader.ReadInt32(); // Version
            var file = new VoxFile();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MAIN") throw new InvalidDataException("Chunk MAIN fehlt.");
            var contentSize = reader.ReadInt32();
            var childrenSize = reader.ReadInt32();
            reader.ReadBytes(contentSize);
            file.ReadChunks(reader, stream.Position + childrenSize);
            return file;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Die .vox-Datei ist unvollständig.");
        }
    }

    public static VoxFile Load(string path)
    {
        using var file = File.OpenRead(path);
        return Read(file);
    }

    /// <summary>Alle sichtbaren Modelle mit ihrer Lage. Ohne Szenengraph (alte Dateien): jedes Modell am Ursprung.</summary>
    public List<VoxInstance> GetInstances()
    {
        var result = new List<VoxInstance>();
        if (_nodes.Count == 0 || !_nodes.ContainsKey(0))
        {
            foreach (var m in Models) result.Add(new VoxInstance(m, VoxTransform.Identity));
            return result;
        }
        Walk(0, VoxTransform.Identity, result, 0);
        return result;
    }

    private void Walk(int nodeId, VoxTransform parent, List<VoxInstance> result, int depth)
    {
        if (depth > 64 || !_nodes.TryGetValue(nodeId, out var node)) throw new InvalidDataException($"Ungültiger Szenengraph (Knoten {nodeId}).");
        switch (node)
        {
            case TransformNode t:
                if (t.Hidden || _hiddenLayers.Contains(t.LayerId)) return;
                Walk(t.Child, parent.Then(t.Transform), result, depth + 1);
                break;
            case GroupNode g:
                foreach (var c in g.Children) Walk(c, parent, result, depth + 1);
                break;
            case ShapeNode s:
                foreach (var modelId in s.Models)
                {
                    if (modelId < 0 || modelId >= Models.Count) throw new InvalidDataException($"Szenengraph verweist auf fehlendes Modell {modelId}.");
                    result.Add(new VoxInstance(Models[modelId], parent));
                }
                break;
        }
    }

    private void ReadChunks(BinaryReader r, long end)
    {
        (int X, int Y, int Z)? pendingSize = null;
        while (r.BaseStream.Position < end)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var contentSize = r.ReadInt32();
            var childrenSize = r.ReadInt32();
            var contentEnd = r.BaseStream.Position + contentSize;

            switch (id)
            {
                case "SIZE":
                    pendingSize = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    break;
                case "XYZI":
                    if (pendingSize == null) throw new InvalidDataException("XYZI ohne vorheriges SIZE.");
                    var count = r.ReadInt32();
                    var voxels = new List<Voxel>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var b = r.ReadBytes(4);
                        if (b.Length < 4) throw new EndOfStreamException();
                        voxels.Add(new Voxel(b[0], b[1], b[2], b[3]));
                    }
                    Models.Add(new VoxModel(pendingSize.Value.X, pendingSize.Value.Y, pendingSize.Value.Z, voxels));
                    pendingSize = null;
                    break;
                case "RGBA":
                    // Eintrag i gehört zu Farbindex i + 1
                    for (var i = 0; i < 255; i++)
                    {
                        var c = r.ReadBytes(4);
                        Palette[i + 1] = (uint)(c[0] << 16 | c[1] << 8 | c[2]);
                    }
                    break;
                case "nTRN":
                    ReadTransform(r);
                    break;
                case "nGRP":
                {
                    var nodeId = r.ReadInt32();
                    ReadDict(r);
                    var n = r.ReadInt32();
                    var children = new int[n];
                    for (var i = 0; i < n; i++) children[i] = r.ReadInt32();
                    _nodes[nodeId] = new GroupNode(children);
                    break;
                }
                case "nSHP":
                {
                    var nodeId = r.ReadInt32();
                    ReadDict(r);
                    var n = r.ReadInt32();
                    var models = new int[n];
                    for (var i = 0; i < n; i++)
                    {
                        models[i] = r.ReadInt32();
                        ReadDict(r);
                    }
                    _nodes[nodeId] = new ShapeNode(models);
                    break;
                }
                case "LAYR":
                {
                    var layerId = r.ReadInt32();
                    var attributes = ReadDict(r);
                    if (attributes.TryGetValue("_hidden", out var h) && h == "1") _hiddenLayers.Add(layerId);
                    break;
                }
            }

            // Unbekannte oder nicht ganz gelesene Chunks (MATL, rOBJ, NOTE, …) überspringen
            r.BaseStream.Position = contentEnd + childrenSize;
        }
    }

    private void ReadTransform(BinaryReader r)
    {
        var nodeId = r.ReadInt32();
        var attributes = ReadDict(r);
        var child = r.ReadInt32();
        r.ReadInt32(); // reserviert
        var layerId = r.ReadInt32();
        var frames = r.ReadInt32();
        var transform = VoxTransform.Identity;
        for (var f = 0; f < frames; f++)
        {
            var frame = ReadDict(r);
            if (f != 0) continue; // Animationen: nur das erste Bild zählt
            var rotation = frame.TryGetValue("_r", out var rs) ? DecodeRotation(byte.Parse(rs)) : VoxTransform.Identity.Rotation;
            int tx = 0, ty = 0, tz = 0;
            if (frame.TryGetValue("_t", out var ts))
            {
                var parts = ts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                tx = int.Parse(parts[0]);
                ty = int.Parse(parts[1]);
                tz = int.Parse(parts[2]);
            }
            transform = new VoxTransform(rotation, tx, ty, tz);
        }
        var hidden = attributes.TryGetValue("_hidden", out var h) && h == "1";
        _nodes[nodeId] = new TransformNode(child, layerId, hidden, transform);
    }

    /// <summary>
    /// Drehung aus einem Byte: Bit 0–1 = Spalte der 1 in Zeile 0, Bit 2–3 = Spalte in Zeile 1
    /// (Zeile 2 bekommt die übrige Spalte), Bit 4/5/6 = Vorzeichen der Zeilen 0/1/2 (1 = negativ).
    /// </summary>
    internal static int[] DecodeRotation(byte r)
    {
        int c0 = r & 3, c1 = (r >> 2) & 3;
        if (c0 > 2 || c1 > 2 || c0 == c1) throw new InvalidDataException($"Ungültige Drehung {r}.");
        var c2 = 3 - c0 - c1;
        var m = new int[9];
        m[c0] = (r & 16) != 0 ? -1 : 1;
        m[3 + c1] = (r & 32) != 0 ? -1 : 1;
        m[6 + c2] = (r & 64) != 0 ? -1 : 1;
        return m;
    }

    private static Dictionary<string, string> ReadDict(BinaryReader r)
    {
        var n = r.ReadInt32();
        var dict = new Dictionary<string, string>();
        for (var i = 0; i < n; i++)
        {
            var key = ReadString(r);
            dict[key] = ReadString(r);
        }
        return dict;
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length < 0 || length > 1 << 20) throw new InvalidDataException("Ungültige Textlänge.");
        return Encoding.UTF8.GetString(r.ReadBytes(length));
    }

    /// <summary>
    /// MagicaVoxels Standardpalette (für Dateien ohne RGBA-Chunk): ein 6 × 6 × 6-Farbwürfel
    /// (Werte FF, CC, 99, 66, 33, 00; Blau ändert sich am schnellsten, ohne Schwarz),
    /// danach je 10 Abstufungen Rot, Grün, Blau und Grau (EE … 11).
    /// </summary>
    internal static uint[] DefaultPalette()
    {
        var p = new uint[256];
        var levels = new uint[] { 0xFF, 0xCC, 0x99, 0x66, 0x33, 0x00 };
        var i = 1;
        foreach (var red in levels)
            foreach (var green in levels)
                foreach (var blue in levels)
                    if (i < 216) p[i++] = red << 16 | green << 8 | blue;
        var ramp = new uint[] { 0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
        foreach (var v in ramp) p[i++] = v << 16;
        foreach (var v in ramp) p[i++] = v << 8;
        foreach (var v in ramp) p[i++] = v;
        foreach (var v in ramp) p[i++] = v << 16 | v << 8 | v;
        return p;
    }

    private abstract record SceneNode;
    private sealed record TransformNode(int Child, int LayerId, bool Hidden, VoxTransform Transform) : SceneNode;
    private sealed record GroupNode(int[] Children) : SceneNode;
    private sealed record ShapeNode(int[] Models) : SceneNode;
}
