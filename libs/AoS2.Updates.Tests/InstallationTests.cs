namespace AoS2.Updates.Tests;

public sealed class InstallationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _storage;

    private static readonly Dictionary<string, string> Files = new()
    {
        ["AoS2.exe"] = "exe",
        ["AoS2_Data/level0"] = "level",
        ["AoS2_Data/Managed/Game.dll"] = "code",
    };

    public InstallationTests()
    {
        _storage = _temp.Combine("storage");
    }

    public void Dispose() => _temp.Dispose();

    private async Task<string> InstallAsync(string directory)
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var installer = new GameInstaller(new DirectoryUpdateSource(_storage), directory);
        await installer.ApplyAsync(await installer.CheckAsync(await installer.FetchManifestAsync(), verifyAll: false));
        return installer.InstallDirectory;
    }

    [Fact]
    public void Resolve_FolderWithOtherContent_AppendsGameFolder()
    {
        var spiele = _temp.Combine("Spiele");
        Directory.CreateDirectory(Path.Combine(spiele, "Anderes Spiel"));

        Assert.Equal(Path.Combine(spiele, "Ace of Spades 2"), Installation.ResolveGameDirectory(spiele));
    }

    [Fact]
    public void Resolve_EmptyFolder_AppendsGameFolder()
    {
        var empty = _temp.Combine("Leer");
        Directory.CreateDirectory(empty);

        Assert.Equal(Path.Combine(empty, "Ace of Spades 2"), Installation.ResolveGameDirectory(empty));
    }

    [Fact]
    public void Resolve_FolderNamedLikeGame_IsUsedDirectly()
    {
        var folder = _temp.Combine("Spiele", "ace of spades 2");

        Assert.Equal(folder, Installation.ResolveGameDirectory(folder + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Resolve_ExistingInstallation_IsUsedDirectly()
    {
        var game = await InstallAsync(_temp.Combine("MeinSpiel"));

        Assert.Equal(game, Installation.ResolveGameDirectory(game));
    }

    [Fact]
    public async Task Move_MovesAllFilesAndState()
    {
        var from = await InstallAsync(_temp.Combine("alt", "Ace of Spades 2"));
        var to = _temp.Combine("neu", "Ace of Spades 2");

        await Installation.MoveAsync(from, to);

        Assert.False(Directory.Exists(from));
        Assert.Equal("code", await File.ReadAllTextAsync(Path.Combine(to, "AoS2_Data", "Managed", "Game.dll")));
        Assert.True(Installation.IsInstalled(to));

        // Nach dem Umzug ist nichts neu zu laden.
        var installer = new GameInstaller(new DirectoryUpdateSource(_storage), to);
        var plan = await installer.CheckAsync(await installer.FetchManifestAsync(), verifyAll: false);
        Assert.True(plan.IsUpToDate);
    }

    [Fact]
    public async Task Move_KeepsForeignFilesAtOldLocation()
    {
        var from = await InstallAsync(_temp.Combine("alt"));
        await File.WriteAllTextAsync(Path.Combine(from, "notizen.txt"), "fremd");
        var to = _temp.Combine("neu");

        await Installation.MoveAsync(from, to);

        Assert.True(File.Exists(Path.Combine(from, "notizen.txt")));
        Assert.False(File.Exists(Path.Combine(to, "notizen.txt")));
        Assert.False(File.Exists(Path.Combine(from, "AoS2.exe")));
    }

    [Fact]
    public async Task Move_TargetNotEmpty_ThrowsAndLeavesInstallIntact()
    {
        var from = await InstallAsync(_temp.Combine("alt"));
        var to = _temp.Combine("neu");
        Directory.CreateDirectory(to);
        await File.WriteAllTextAsync(Path.Combine(to, "x.txt"), "x");

        await Assert.ThrowsAsync<UpdateException>(() => Installation.MoveAsync(from, to));

        Assert.True(File.Exists(Path.Combine(from, "AoS2.exe")));
    }

    [Fact]
    public async Task Move_IntoOwnSubfolder_Throws()
    {
        var from = await InstallAsync(_temp.Combine("alt"));

        await Assert.ThrowsAsync<UpdateException>(() => Installation.MoveAsync(from, Path.Combine(from, "sub")));
    }

    [Fact]
    public async Task Move_Cancelled_RollsBack()
    {
        var from = await InstallAsync(_temp.Combine("alt"));
        var to = _temp.Combine("neu");
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirstReport(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installation.MoveAsync(from, to, progress, cts.Token));

        Assert.True(File.Exists(Path.Combine(from, "AoS2.exe")));
        Assert.True(Installation.IsInstalled(from));
        Assert.False(Directory.Exists(to));
    }

    [Fact]
    public async Task Remove_DeletesOnlyInstalledFiles()
    {
        var parent = _temp.Combine("Spiele");
        var game = await InstallAsync(Path.Combine(parent, "Ace of Spades 2"));
        await File.WriteAllTextAsync(Path.Combine(parent, "anderes-spiel.exe"), "fremd");

        Assert.True(await Installation.RemoveAsync(game));

        Assert.False(Directory.Exists(game));
        Assert.True(File.Exists(Path.Combine(parent, "anderes-spiel.exe")));
    }

    [Fact]
    public async Task Remove_ForeignFileInGameFolder_KeepsFolder()
    {
        var game = await InstallAsync(_temp.Combine("game"));
        await File.WriteAllTextAsync(Path.Combine(game, "screenshot.png"), "fremd");

        Assert.False(await Installation.RemoveAsync(game));

        Assert.Equal(["screenshot.png"], Directory.EnumerateFileSystemEntries(game).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Remove_NoInstallation_TouchesNothing()
    {
        var folder = _temp.Combine("fremd");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "AoS2.exe"), "nicht von uns");

        Assert.False(await Installation.RemoveAsync(folder));

        Assert.True(File.Exists(Path.Combine(folder, "AoS2.exe")));
    }

    private sealed class CancelAfterFirstReport(CancellationTokenSource cts) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => cts.Cancel();
    }
}
