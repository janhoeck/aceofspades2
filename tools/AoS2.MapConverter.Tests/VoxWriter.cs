using System.Text;

namespace AoS2.MapConverter.Tests;

/// <summary>Schreibt kleine .vox-Dateien für Tests (Modelle, Palette, optional Szenengraph).</summary>
internal sealed class VoxWriter
{
    private readonly List<(int X, int Y, int Z, List<Voxel> Voxels)> _models = new();
    private readonly List<(int TX, int TY, int TZ, byte Rotation, bool Hidden)> _placements = new();
    private readonly uint[] _palette = new uint[256];

    public bool WithSceneGraph { get; set; } = true;

    public VoxWriter Color(byte index, uint rgb)
    {
        _palette[index] = rgb;
        return this;
    }

    public VoxWriter Model(int sx, int sy, int sz, IEnumerable<Voxel> voxels, int tx = 0, int ty = 0, int tz = 0, byte rotation = 4, bool hidden = false)
    {
        _models.Add((sx, sy, sz, voxels.ToList()));
        _placements.Add((tx, ty, tz, rotation, hidden));
        return this;
    }

    public MemoryStream ToStream()
    {
        var children = new MemoryStream();
        foreach (var m in _models)
        {
            Chunk(children, "SIZE", w => { w.Write(m.X); w.Write(m.Y); w.Write(m.Z); });
            Chunk(children, "XYZI", w =>
            {
                w.Write(m.Voxels.Count);
                foreach (var v in m.Voxels) w.Write(new[] { (byte)v.X, (byte)v.Y, (byte)v.Z, v.ColorIndex });
            });
        }

        if (WithSceneGraph)
        {
            // Wurzel: nTRN 0 → nGRP 1 → je Modell nTRN (2 + 2i) → nSHP (3 + 2i)
            var groupChildren = Enumerable.Range(0, _models.Count).Select(i => 2 + 2 * i).ToArray();
            Chunk(children, "nTRN", w => Transform(w, 0, 1, new Dictionary<string, string>(), new Dictionary<string, string>()));
            Chunk(children, "nGRP", w =>
            {
                w.Write(1);
                Dict(w, new Dictionary<string, string>());
                w.Write(groupChildren.Length);
                foreach (var c in groupChildren) w.Write(c);
            });
            for (var i = 0; i < _models.Count; i++)
            {
                var p = _placements[i];
                var attributes = p.Hidden ? new Dictionary<string, string> { ["_hidden"] = "1" } : new Dictionary<string, string>();
                var frame = new Dictionary<string, string> { ["_t"] = $"{p.TX} {p.TY} {p.TZ}", ["_r"] = p.Rotation.ToString() };
                var index = i;
                Chunk(children, "nTRN", w => Transform(w, 2 + 2 * index, 3 + 2 * index, attributes, frame));
                Chunk(children, "nSHP", w =>
                {
                    w.Write(3 + 2 * index);
                    Dict(w, new Dictionary<string, string>());
                    w.Write(1);
                    w.Write(index);
                    Dict(w, new Dictionary<string, string>());
                });
            }
        }

        Chunk(children, "RGBA", w =>
        {
            for (var i = 1; i <= 256; i++)
            {
                var c = i < 256 ? _palette[i] : 0;
                w.Write(new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)255 });
            }
        });

        var result = new MemoryStream();
        var writer = new BinaryWriter(result);
        writer.Write(Encoding.ASCII.GetBytes("VOX "));
        writer.Write(200);
        writer.Write(Encoding.ASCII.GetBytes("MAIN"));
        writer.Write(0);
        writer.Write((int)children.Length);
        writer.Write(children.ToArray());
        result.Position = 0;
        return result;
    }

    private static void Transform(BinaryWriter w, int id, int child, Dictionary<string, string> attributes, Dictionary<string, string> frame)
    {
        w.Write(id);
        Dict(w, attributes);
        w.Write(child);
        w.Write(-1);
        w.Write(0); // Ebene
        w.Write(1); // ein Bild
        Dict(w, frame);
    }

    private static void Chunk(Stream s, string id, Action<BinaryWriter> content)
    {
        var body = new MemoryStream();
        content(new BinaryWriter(body));
        var w = new BinaryWriter(s);
        w.Write(Encoding.ASCII.GetBytes(id));
        w.Write((int)body.Length);
        w.Write(0);
        w.Write(body.ToArray());
    }

    private static void Dict(BinaryWriter w, Dictionary<string, string> d)
    {
        w.Write(d.Count);
        foreach (var (k, v) in d)
        {
            Str(w, k);
            Str(w, v);
        }
    }

    private static void Str(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write(bytes.Length);
        w.Write(bytes);
    }
}
