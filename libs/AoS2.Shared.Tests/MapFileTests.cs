using System.Text;
using AoS2.Shared.Maps;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class MapFileTests
{
    private static MapMetadata SampleMetadata()
    {
        var meta = new MapMetadata { Name = "Test", Author = "Jan", CombatZone = new BlockBox(0, 0, 8, 64, 32, 56) };
        meta.Teams[Team.Blue] = new TeamSpawn { Zone = new BlockBox(0, 0, 0, 64, 32, 8), Points = { new Vec3(4.5f, 4, 4.5f) } };
        meta.Teams[Team.Green] = new TeamSpawn { Zone = new BlockBox(0, 0, 56, 64, 32, 64), Points = { new Vec3(4.5f, 4, 60.5f), new Vec3(8.5f, 4, 60.5f) } };
        return meta;
    }

    private static byte[] Save(VoxelWorld world, MapMetadata meta, BlockRegistry registry)
    {
        using var stream = new MemoryStream();
        MapFile.Write(stream, world, meta, registry);
        return stream.ToArray();
    }

    [Fact]
    public void WriteThenRead_ReturnsSameBlocksAndMetadata()
    {
        var world = TestData.FlatWorld(64, 40, 64);
        var random = new Random(3);
        for (var i = 0; i < 5000; i++) world.SetBlock(random.Next(64), random.Next(40), random.Next(64), (byte)random.Next(0, 4));

        var bytes = Save(world, SampleMetadata(), TestData.Registry);
        var map = MapFile.Read(new MemoryStream(bytes), TestData.Registry);

        Assert.Equal((64, 40, 64), (map.World.SizeX, map.World.SizeY, map.World.SizeZ));
        for (var y = 0; y < 40; y++)
            for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                    Assert.Equal(world.GetBlock(x, y, z), map.World.GetBlock(x, y, z));

        Assert.Equal("Test", map.Metadata.Name);
        Assert.Equal("Jan", map.Metadata.Author);
        Assert.Equal(new BlockBox(0, 0, 8, 64, 32, 56), map.Metadata.CombatZone);
        Assert.Equal(new Vec3(4.5f, 4, 4.5f), map.Metadata.Teams[Team.Blue].Points[0]);
        Assert.Equal(2, map.Metadata.Teams[Team.Green].Points.Count);
        Assert.Equal(3, map.Metadata.AllSpawnPoints().Count());
    }

    [Fact]
    public void Read_MapsBlocksByName_NotById()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(1, 1, 1, TestData.Stone);
        var bytes = Save(world, new MapMetadata(), TestData.Registry);

        // Andere blocks.json: "stone" hat hier die ID 77.
        var other = BlockRegistry.Parse("""{ "blocks": [ { "id": 77, "name": "stone", "textures": { "all": "s" } } ] }""");
        var map = MapFile.Read(new MemoryStream(bytes), other);

        Assert.Equal(77, map.World.GetBlock(1, 1, 1));
    }

    [Fact]
    public void Read_UnknownBlockName_Throws()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(1, 1, 1, TestData.Dirt);
        var bytes = Save(world, new MapMetadata(), TestData.Registry);
        var other = BlockRegistry.Parse("""{ "blocks": [ { "id": 2, "name": "stone", "textures": { "all": "s" } } ] }""");

        var e = Assert.Throws<InvalidDataException>(() => MapFile.Read(new MemoryStream(bytes), other));
        Assert.Contains("dirt", e.Message);
    }

    [Fact]
    public void Write_FlatWorld_IsSmall()
    {
        // 256 × 64 × 256 = 4 Mio. Blöcke; Lauflängen + Deflate machen daraus wenige Bytes.
        var bytes = Save(TestData.FlatWorld(256, 64, 256, 20), new MapMetadata(), TestData.Registry);
        Assert.True(bytes.Length < 4096, $"{bytes.Length} Bytes");
    }

    [Fact]
    public void Read_WrongMagic_Throws()
    {
        var bytes = Encoding.ASCII.GetBytes("NOTAMAP!xxxxxxxxxxxxxxxx");
        Assert.Throws<InvalidDataException>(() => MapFile.Read(new MemoryStream(bytes), TestData.Registry));
    }

    [Fact]
    public void Read_Truncated_Throws()
    {
        var bytes = Save(TestData.FlatWorld(), SampleMetadata(), TestData.Registry);
        Assert.Throws<InvalidDataException>(() => MapFile.Read(new MemoryStream(bytes[..(bytes.Length - 10)]), TestData.Registry));
    }

    [Fact]
    public void Metadata_InvalidBox_Throws()
    {
        Assert.Throws<FormatException>(() => MapMetadata.Parse("""{ "combatZone": { "min": [0, 0, 0], "max": [0, 5, 5] } }"""));
        Assert.Throws<FormatException>(() => MapMetadata.Parse("""{ "teams": { "red": { "zone": { "min": [0,0,0], "max": [1,1,1] } } } }"""));
    }
}
