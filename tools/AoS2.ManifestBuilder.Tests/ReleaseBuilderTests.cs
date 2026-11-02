using System.Security.Cryptography;
using System.Text;
using AoS2.Updates;

namespace AoS2.ManifestBuilder.Tests;

public sealed class ReleaseBuilderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aos2-builder-tests-").FullName;
    private readonly string _build;
    private readonly string _release;

    public ReleaseBuilderTests()
    {
        _build = Path.Combine(_root, "build");
        _release = Path.Combine(_root, "release");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteBuildFile(string path, string content)
    {
        var fullPath = Path.Combine(_build, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private BuilderOptions Options(string version = "0.1.0", params string[] extraExcludes) =>
        new(_build, _release, version, BuilderOptions.DefaultExecutable, [.. BuilderOptions.DefaultExcludes, .. extraExcludes]);

    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task CreateManifest_ListsAllFilesSortedWithHashAndSize()
    {
        WriteBuildFile("AoS2.exe", "exe");
        WriteBuildFile("UnityPlayer.dll", "player!");
        WriteBuildFile("AoS2_Data/Managed/Assembly-CSharp.dll", "code");

        var manifest = await ReleaseBuilder.CreateManifestAsync(Options());

        Assert.Equal("0.1.0", manifest.GameVersion);
        Assert.Equal("AoS2.exe", manifest.Executable);
        Assert.Equal(
            [
                new ManifestFile("AoS2.exe", 3, Sha256("exe")),
                new ManifestFile("AoS2_Data/Managed/Assembly-CSharp.dll", 4, Sha256("code")),
                new ManifestFile("UnityPlayer.dll", 7, Sha256("player!")),
            ],
            manifest.Files);
    }

    [Fact]
    public async Task CreateManifest_SkipsUnityDoNotShipFoldersAndCustomExcludes()
    {
        WriteBuildFile("AoS2.exe", "exe");
        WriteBuildFile("AoS2_BurstDebugInformation_DoNotShip/Data/lib.txt", "debug");
        WriteBuildFile("AoS2_BackUpThisFolder_ButDontShipItInYourProducts/x.cpp", "il2cpp");
        WriteBuildFile("AoS2_BackUpThisFolder_ButDontShipItWithYourGame/x86_64/lib.pdb", "unity 6.6");
        WriteBuildFile("AoS2_Data/DoNotShipMe.txt", "normaler Dateiname bleibt");
        WriteBuildFile("symbols.pdb", "pdb");

        var manifest = await ReleaseBuilder.CreateManifestAsync(Options("0.1.0", "*.pdb"));

        Assert.Equal(["AoS2.exe", "AoS2_Data/DoNotShipMe.txt"], manifest.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task CreateManifest_MissingExecutable_Throws()
    {
        WriteBuildFile("Other.exe", "x");

        await Assert.ThrowsAsync<InvalidManifestException>(() => ReleaseBuilder.CreateManifestAsync(Options()));
    }

    [Fact]
    public async Task CreateManifest_MissingInput_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ReleaseBuilder.CreateManifestAsync(Options()));
    }

    [Fact]
    public async Task WriteRelease_WritesObjectsAndBothManifests()
    {
        WriteBuildFile("AoS2.exe", "exe");
        WriteBuildFile("AoS2_Data/a", "same");
        WriteBuildFile("AoS2_Data/b", "same");
        var options = Options();
        var manifest = await ReleaseBuilder.CreateManifestAsync(options);

        var written = await ReleaseBuilder.WriteReleaseAsync(manifest, options);

        Assert.Equal(2, written); // „a“ und „b“ haben denselben Inhalt → ein Objekt.
        Assert.Equal("exe", File.ReadAllText(Path.Combine(_release, UpdateLayout.ObjectPath(Sha256("exe")))));
        Assert.True(File.Exists(Path.Combine(_release, "game", "manifests", "0.1.0.json")));

        await using var stream = File.OpenRead(Path.Combine(_release, UpdateLayout.ManifestPath));
        var read = await ManifestSerializer.ReadAsync(stream);
        Assert.Equal(manifest.Files, read.Files);
    }

    [Fact]
    public async Task WriteRelease_SecondVersion_OnlyWritesNewObjects()
    {
        WriteBuildFile("AoS2.exe", "exe v1");
        WriteBuildFile("UnityPlayer.dll", "player");
        var v1 = Options("0.1.0");
        await ReleaseBuilder.WriteReleaseAsync(await ReleaseBuilder.CreateManifestAsync(v1), v1);

        WriteBuildFile("AoS2.exe", "exe v2");
        var v2 = Options("0.2.0");
        var written = await ReleaseBuilder.WriteReleaseAsync(await ReleaseBuilder.CreateManifestAsync(v2), v2);

        Assert.Equal(1, written);
        Assert.True(File.Exists(Path.Combine(_release, "game", "manifests", "0.1.0.json")));
        Assert.True(File.Exists(Path.Combine(_release, "game", "manifests", "0.2.0.json")));
    }

    [Fact]
    public async Task Release_CanBeInstalledByGameInstaller()
    {
        WriteBuildFile("AoS2.exe", "exe");
        WriteBuildFile("AoS2_Data/level0", "level");
        var options = Options();
        await ReleaseBuilder.WriteReleaseAsync(await ReleaseBuilder.CreateManifestAsync(options), options);

        var installer = new GameInstaller(new DirectoryUpdateSource(_release), Path.Combine(_root, "game"));
        var manifest = await installer.FetchManifestAsync();
        await installer.ApplyAsync(await installer.CheckAsync(manifest, verifyAll: false));

        Assert.Equal("level", File.ReadAllText(Path.Combine(_root, "game", "AoS2_Data", "level0")));
    }
}
