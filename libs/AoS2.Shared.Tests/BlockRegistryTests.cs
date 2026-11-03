using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class BlockRegistryTests
{
    [Fact]
    public void Parse_FaceTextures_SpecificKeysWin()
    {
        var registry = BlockRegistry.Parse("""
            { "blocks": [ { "id": 4, "name": "grass", "displayName": "Gras",
              "textures": { "all": "dirt", "top": ["top1", "top2"], "side": "side" } } ] }
            """);

        var grass = registry.Get("grass");
        Assert.Equal(4, grass.Id);
        Assert.Equal("Gras", grass.DisplayName);
        Assert.Equal(new[] { "top1", "top2" }, grass.GetTextures(BlockFace.Top));
        Assert.Equal(new[] { "dirt" }, grass.GetTextures(BlockFace.Bottom));
        Assert.Equal(new[] { "side" }, grass.GetTextures(BlockFace.PosX));
        Assert.Equal(new[] { "side" }, grass.GetTextures(BlockFace.NegZ));
        Assert.Same(grass, registry.Get((byte)4));
        Assert.Null(registry.Get((byte)5));
    }

    [Theory]
    [InlineData("""{ "blocks": [ { "id": 0, "name": "a", "textures": { "all": "x" } } ] }""")]
    [InlineData("""{ "blocks": [ { "id": 256, "name": "a", "textures": { "all": "x" } } ] }""")]
    [InlineData("""{ "blocks": [ { "id": 1, "name": "a", "textures": { "all": "x" } }, { "id": 1, "name": "b", "textures": { "all": "x" } } ] }""")]
    [InlineData("""{ "blocks": [ { "id": 1, "name": "a", "textures": { "all": "x" } }, { "id": 2, "name": "a", "textures": { "all": "x" } } ] }""")]
    [InlineData("""{ "blocks": [ { "id": 1, "name": "a", "textures": { "top": "x" } } ] }""")]
    [InlineData("""{ "blocks": [ { "id": 1, "name": "a" } ] }""")]
    public void Parse_InvalidData_Throws(string json)
    {
        Assert.Throws<FormatException>(() => BlockRegistry.Parse(json));
    }

    [Fact]
    public void RealBlocksJson_IsValid()
    {
        var registry = BlockRegistry.Parse(File.ReadAllText(TestData.RepoFile("data/blocks.json")));

        Assert.True(registry.Get("bedrock").Indestructible);
        Assert.True(registry.Get("spawn_blue").Indestructible);
        Assert.True(registry.Get("spawn_green").Indestructible);
        Assert.Contains(registry.All, b => b.Buildable);
        foreach (var name in new[] { "grass", "dirt", "stone" }) Assert.False(registry.Get(name).Indestructible);
    }

    [Fact]
    public void RealBlocksJson_AllTexturesExist()
    {
        // Prüft nur, ob es die Datei gibt; ohne Git LFS sind die PNGs nur Zeiger-Dateien, das reicht hier.
        var registry = BlockRegistry.Parse(File.ReadAllText(TestData.RepoFile("data/blocks.json")));
        foreach (var block in registry.All)
            for (var f = 0; f < BlockFaces.Count; f++)
                foreach (var texture in block.GetTextures((BlockFace)f))
                    Assert.True(File.Exists(TestData.RepoFile($"assets-src/textures/blocks/{texture}.png")), $"{block.Name}: Textur {texture}.png fehlt");
    }
}
