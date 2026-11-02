using System.Text;

namespace AoS2.Updates.Tests;

public class ManifestSerializerTests
{
    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task WriteThenRead_ReturnsSameManifest()
    {
        var manifest = TestRelease.CreateManifest("1.2.3", new Dictionary<string, string>
        {
            ["AoS2.exe"] = "exe",
            ["AoS2_Data/level0"] = "level",
        });

        using var stream = new MemoryStream();
        await ManifestSerializer.WriteAsync(manifest, stream);
        stream.Position = 0;
        var read = await ManifestSerializer.ReadAsync(stream);

        Assert.Equal(manifest.GameVersion, read.GameVersion);
        Assert.Equal(manifest.Executable, read.Executable);
        Assert.Equal(manifest.Files, read.Files);
    }

    [Fact]
    public async Task Write_UsesCamelCase()
    {
        var manifest = TestRelease.CreateManifest("1.0.0", new Dictionary<string, string> { ["AoS2.exe"] = "x" });

        using var stream = new MemoryStream();
        await ManifestSerializer.WriteAsync(manifest, stream);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"formatVersion\": 1", json);
        Assert.Contains("\"gameVersion\": \"1.0.0\"", json);
        Assert.Contains("\"sha256\":", json);
        Assert.DoesNotContain("totalSize", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_InvalidJson_ThrowsInvalidManifest()
    {
        await Assert.ThrowsAsync<InvalidManifestException>(() => ManifestSerializer.ReadAsync(Utf8("{ nope")));
    }

    [Fact]
    public async Task Read_MissingRequiredProperty_ThrowsInvalidManifest()
    {
        await Assert.ThrowsAsync<InvalidManifestException>(() => ManifestSerializer.ReadAsync(Utf8("""{ "gameVersion": "1.0.0", "files": [] }""")));
    }

    [Fact]
    public async Task Read_UnsafePath_ThrowsInvalidManifest()
    {
        var json = $$"""
            {
              "formatVersion": 1,
              "gameVersion": "1.0.0",
              "executable": "../evil.exe",
              "files": [ { "path": "../evil.exe", "size": 1, "sha256": "{{new string('0', 64)}}" } ]
            }
            """;
        await Assert.ThrowsAsync<InvalidManifestException>(() => ManifestSerializer.ReadAsync(Utf8(json)));
    }
}
