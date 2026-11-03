using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Voxels;

namespace AoS2.MapConverter.Tests;

public class ConverterTests
{
    private const uint Green = 0x6b8f3a;
    private const uint Grey = 0x7a746b;

    private static readonly BlockRegistry Registry = BlockRegistry.Parse("""
        { "blocks": [
          { "id": 2, "name": "stone", "textures": { "all": "s" } },
          { "id": 4, "name": "grass", "textures": { "all": "g" } }
        ] }
        """);

    private static readonly ColorMapping Mapping = ColorMapping.Parse("""{ "colors": { "#6b8f3a": "grass", "#7A746B": "stone" } }""", Registry);

    private static VoxWriter Writer() => new VoxWriter().Color(1, Green).Color(2, Grey);

    [Fact]
    public void SingleModel_SwapsAxes_ZUpBecomesYUp()
    {
        // Voxel (x=1, y=2, z=3) in MagicaVoxel = Block (x=1, Höhe=3, z=2) im Spiel
        var vox = VoxFile.Read(Writer().Model(4, 4, 4, new[] { new Voxel(0, 0, 0, 2), new Voxel(1, 2, 3, 1) }).ToStream());
        var world = VoxImporter.Convert(vox, Mapping);

        Assert.Equal((2, 4, 3), (world.SizeX, world.SizeY, world.SizeZ));
        Assert.Equal(2, world.GetBlock(0, 0, 0));
        Assert.Equal(4, world.GetBlock(1, 3, 2));
    }

    [Fact]
    public void WithoutSceneGraph_ModelsAtOrigin()
    {
        var writer = Writer().Model(2, 2, 2, new[] { new Voxel(1, 1, 1, 1) });
        writer.WithSceneGraph = false;
        var world = VoxImporter.Convert(VoxFile.Read(writer.ToStream()), Mapping, (8, 8, 8));
        Assert.Equal(4, world.GetBlock(0, 0, 0)); // auf 0/0/0 verschoben
    }

    [Fact]
    public void TwoModels_KeepRelativePosition()
    {
        var vox = VoxFile.Read(Writer()
            .Model(2, 2, 2, new[] { new Voxel(0, 0, 0, 2) }, tx: 0, ty: 0, tz: 0)
            .Model(2, 2, 2, new[] { new Voxel(0, 0, 0, 1) }, tx: 10, ty: 5, tz: 3)
            .ToStream());

        var world = VoxImporter.Convert(vox, Mapping);

        Assert.Equal(2, vox.GetInstances().Count);
        Assert.Equal((11, 4, 6), (world.SizeX, world.SizeY, world.SizeZ));
        Assert.Equal(2, world.GetBlock(0, 0, 0));
        Assert.Equal(4, world.GetBlock(10, 3, 5)); // MagicaVoxel (10, 5, 3) → Spiel (10, 3, 5)
    }

    [Fact]
    public void HiddenModel_IsSkipped()
    {
        var vox = VoxFile.Read(Writer()
            .Model(2, 2, 2, new[] { new Voxel(0, 0, 0, 2) })
            .Model(2, 2, 2, new[] { new Voxel(0, 0, 0, 1) }, tx: 10, hidden: true)
            .ToStream());

        var world = VoxImporter.Convert(vox, Mapping);
        Assert.Equal((1, 1, 1), (world.SizeX, world.SizeY, world.SizeZ));
    }

    [Fact]
    public void RotatedModel_IsRotated()
    {
        // Drehung um z um 90°: Zeile 0 = (0, -1, 0), Zeile 1 = (1, 0, 0), Zeile 2 = (0, 0, 1)
        // Bits: c0 = 1, c1 = 0, Vorzeichen Zeile 0 negativ → 1 | 0 << 2 | 16 = 17
        var rotation = VoxFile.DecodeRotation(17);
        Assert.Equal(new[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 }, rotation);

        // Ein Stab entlang x (3 lang) wird zu einem Stab entlang y (MagicaVoxel) = z im Spiel
        var vox = VoxFile.Read(Writer().Model(3, 1, 1, new[] { new Voxel(0, 0, 0, 1), new Voxel(1, 0, 0, 1), new Voxel(2, 0, 0, 2) }, rotation: 17).ToStream());
        var world = VoxImporter.Convert(vox, Mapping);

        Assert.Equal((1, 1, 3), (world.SizeX, world.SizeY, world.SizeZ));
        Assert.Equal(2, world.GetBlock(0, 0, 2));
    }

    [Fact]
    public void Identity_RotationByte()
    {
        Assert.Equal(VoxTransform.Identity.Rotation, VoxFile.DecodeRotation(4));
        Assert.Throws<InvalidDataException>(() => VoxFile.DecodeRotation(0)); // zweimal Spalte 0
    }

    [Fact]
    public void UnmappedColor_ListsAllMissingColors()
    {
        var vox = VoxFile.Read(Writer().Color(3, 0xff0000).Model(2, 2, 2, new[] { new Voxel(0, 0, 0, 3), new Voxel(1, 0, 0, 3), new Voxel(1, 1, 1, 1) }).ToStream());
        var e = Assert.Throws<ConversionException>(() => VoxImporter.Convert(vox, Mapping));
        Assert.Contains("#ff0000", e.Message);
        Assert.Contains("2 Voxel", e.Message);
    }

    [Fact]
    public void FixedSize_TooSmall_Throws()
    {
        var vox = VoxFile.Read(Writer().Model(4, 4, 4, new[] { new Voxel(0, 0, 0, 1), new Voxel(3, 0, 0, 1) }).ToStream());
        Assert.Throws<ConversionException>(() => VoxImporter.Convert(vox, Mapping, (2, 2, 2)));
    }

    [Fact]
    public void EmptyFile_Throws()
    {
        var vox = VoxFile.Read(Writer().Model(4, 4, 4, Array.Empty<Voxel>()).ToStream());
        Assert.Throws<ConversionException>(() => VoxImporter.Convert(vox, Mapping));
    }

    [Fact]
    public void NotAVoxFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => VoxFile.Read(new MemoryStream("hallo welt 123"u8.ToArray())));
    }

    [Fact]
    public void DefaultPalette_KnownEntries()
    {
        var p = VoxFile.DefaultPalette();
        Assert.Equal(0xffffffu, p[1]);
        Assert.Equal(0xffffccu, p[2]);
        Assert.Equal(0x000033u, p[215]);
        Assert.Equal(0xee0000u, p[216]);
        Assert.Equal(0x111111u, p[255]);
    }

    [Theory]
    [InlineData("""{ "colors": { "#zzzzzz": "stone" } }""")]
    [InlineData("""{ "colors": { "#123456": "lava" } }""")]
    [InlineData("""{ "colors": { "#123456": "stone", "#123456 ": "stone" } }""")]
    [InlineData("""{ }""")]
    public void Mapping_Invalid_Throws(string json)
    {
        Assert.Throws<FormatException>(() => ColorMapping.Parse(json, Registry));
    }

    [Fact]
    public void ConvertedWorld_SavesAsMap()
    {
        var vox = VoxFile.Read(Writer().Model(4, 4, 4, new[] { new Voxel(0, 0, 0, 2), new Voxel(1, 2, 3, 1) }).ToStream());
        var world = VoxImporter.Convert(vox, Mapping, (32, 16, 32));
        using var stream = new MemoryStream();
        MapFile.Write(stream, world, new MapMetadata { Name = "x" }, Registry);
        stream.Position = 0;

        var map = MapFile.Read(stream, Registry);
        Assert.Equal(4, map.World.GetBlock(1, 3, 2));
    }

    [Fact]
    public void MapChecks_FindsBadSpawnPoints()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(5, 5, 5, 2);
        var meta = new MapMetadata();
        meta.Teams[Team.Blue] = new TeamSpawn { Zone = new BlockBox(0, 0, 0, 10, 10, 10), Points = { new Vec3(5.5f, 5, 5.5f), new Vec3(20, 1, 20) } };
        meta.Teams[Team.Green] = new TeamSpawn { Zone = new BlockBox(0, 0, 0, 40, 10, 10) };

        var e = Assert.Throws<ConversionException>(() => MapChecks.Validate(world, meta));
        Assert.Contains("steckt in Blöcken", e.Message);
        Assert.Contains("nicht in der Spawnzone", e.Message);
        Assert.Contains("ragt aus der Map", e.Message);
        Assert.Contains("keine Spawnpunkte", e.Message);
    }

    [Fact]
    public void Options_Parse()
    {
        var o = ConverterOptions.Parse(new[] { "maps/a.vox", "--mapping", "m.json", "--size", "128,64,128" }, out var error);
        Assert.Null(error);
        Assert.Equal("maps/a.aos2map", o!.Output.Replace('\\', '/'));
        Assert.Equal((128, 64, 128), o.Size);

        Assert.Null(ConverterOptions.Parse(new[] { "a.vox" }, out error));
        Assert.Contains("--mapping", error);
        Assert.Null(ConverterOptions.Parse(new[] { "a.vox", "--mapping", "m", "--size", "1,2" }, out error));
        Assert.NotNull(error);
    }
}
