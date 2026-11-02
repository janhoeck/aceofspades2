namespace AoS2.Updates.Tests;

public sealed class GameInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _storage;
    private readonly string _game;
    private readonly CountingSource _source;
    private readonly GameInstaller _installer;

    private static readonly Dictionary<string, string> Version1 = new()
    {
        ["AoS2.exe"] = "exe v1",
        ["AoS2_Data/level0"] = "level v1",
        ["AoS2_Data/old.asset"] = "wird in v2 entfernt",
        ["UnityPlayer.dll"] = "player",
    };

    private static readonly Dictionary<string, string> Version2 = new()
    {
        ["AoS2.exe"] = "exe v2",
        ["AoS2_Data/level0"] = "level v1",
        ["AoS2_Data/new.asset"] = "neu in v2",
        ["UnityPlayer.dll"] = "player",
    };

    public GameInstallerTests()
    {
        _storage = _temp.Combine("storage");
        _game = _temp.Combine("game");
        _source = new CountingSource(new DirectoryUpdateSource(_storage));
        _installer = new GameInstaller(_source, _game) { RetryDelay = TimeSpan.Zero };
    }

    public void Dispose() => _temp.Dispose();

    private async Task<UpdatePlan> InstallAsync(bool verifyAll = false)
    {
        var manifest = await _installer.FetchManifestAsync();
        var plan = await _installer.CheckAsync(manifest, verifyAll);
        await _installer.ApplyAsync(plan);
        return plan;
    }

    private string GamePath(string relative) => Path.Combine(_game, relative);

    private int ObjectRequests => _source.Requests.Count(r => r.StartsWith("game/objects/"));

    [Fact]
    public async Task FreshInstall_DownloadsAllFiles_AndSavesState()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);

        var plan = await InstallAsync();

        Assert.Equal(4, plan.Downloads.Count);
        foreach (var (path, content) in Version1)
            Assert.Equal(content, await File.ReadAllTextAsync(GamePath(path)));

        var state = await _installer.LoadStateAsync();
        Assert.NotNull(state);
        Assert.Equal("0.1.0", state.GameVersion);
        Assert.Equal("AoS2.exe", state.Executable);
        Assert.Equal(4, state.Files.Count);
        Assert.False(Directory.Exists(Path.Combine(_game, InstallState.FolderName, "download")));
    }

    [Fact]
    public async Task SecondRun_NothingToDownload()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        _source.Requests.Clear();

        var plan = await InstallAsync();

        Assert.True(plan.IsUpToDate);
        Assert.Equal(0, ObjectRequests);
    }

    [Fact]
    public async Task Update_DownloadsOnlyChangedFiles_AndDeletesRemovedOnes()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        await File.WriteAllTextAsync(GamePath("Player.log"), "fremde Datei");
        _source.Requests.Clear();

        await TestRelease.PublishAsync(_storage, "0.2.0", Version2);
        var plan = await InstallAsync();

        Assert.Equal(["AoS2.exe", "AoS2_Data/new.asset"], plan.Downloads.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["AoS2_Data/old.asset"], plan.Deletions);
        Assert.Equal(2, ObjectRequests);
        Assert.Equal("exe v2", await File.ReadAllTextAsync(GamePath("AoS2.exe")));
        Assert.False(File.Exists(GamePath("AoS2_Data/old.asset")));
        Assert.True(File.Exists(GamePath("Player.log")), "Dateien, die der Launcher nicht installiert hat, bleiben.");
        Assert.Equal("0.2.0", (await _installer.LoadStateAsync())!.GameVersion);
    }

    [Fact]
    public async Task Update_RemovesEmptyFolders()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", new Dictionary<string, string> { ["AoS2.exe"] = "a", ["Plugins/x64/lib.dll"] = "b" });
        await InstallAsync();

        await TestRelease.PublishAsync(_storage, "0.2.0", new Dictionary<string, string> { ["AoS2.exe"] = "a" });
        await InstallAsync();

        Assert.False(Directory.Exists(GamePath("Plugins")));
        Assert.True(File.Exists(GamePath("AoS2.exe")));
    }

    [Fact]
    public async Task MissingFile_IsDownloadedAgain()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        File.Delete(GamePath("UnityPlayer.dll"));

        var plan = await InstallAsync();

        Assert.Equal("UnityPlayer.dll", Assert.Single(plan.Downloads).Path);
        Assert.Equal("player", await File.ReadAllTextAsync(GamePath("UnityPlayer.dll")));
    }

    [Fact]
    public async Task ChangedFile_SameSize_IsDetectedByChangedTimestamp()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        await File.WriteAllTextAsync(GamePath("AoS2.exe"), "EXE V1");
        File.SetLastWriteTimeUtc(GamePath("AoS2.exe"), DateTime.UtcNow.AddMinutes(1));

        var manifest = await _installer.FetchManifestAsync();
        var plan = await _installer.CheckAsync(manifest, verifyAll: false);

        Assert.Equal("AoS2.exe", Assert.Single(plan.Downloads).Path);
    }

    [Fact]
    public async Task ChangedFile_SameSizeAndTimestamp_IsOnlyFoundByVerifyAll()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        var path = GamePath("AoS2.exe");
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "EXE V1");
        File.SetLastWriteTimeUtc(path, timestamp);

        var manifest = await _installer.FetchManifestAsync();
        Assert.Empty((await _installer.CheckAsync(manifest, verifyAll: false)).Downloads);

        var plan = await _installer.CheckAsync(manifest, verifyAll: true);
        Assert.Equal("AoS2.exe", Assert.Single(plan.Downloads).Path);

        await _installer.ApplyAsync(plan);
        Assert.Equal("exe v1", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExistingFilesWithoutState_AreHashedAndKept()
    {
        var manifest = await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        Directory.CreateDirectory(GamePath("AoS2_Data"));
        foreach (var (path, content) in Version1)
            await File.WriteAllTextAsync(GamePath(path), content);

        var plan = await _installer.CheckAsync(manifest, verifyAll: false);

        Assert.True(plan.IsUpToDate);
    }

    [Fact]
    public async Task CorruptObject_ThrowsAfterRetries_AndKeepsOldFile()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        await InstallAsync();
        var manifest = await TestRelease.PublishAsync(_storage, "0.2.0", Version2);
        var exe = manifest.Files.Single(f => f.Path == "AoS2.exe");
        await File.WriteAllTextAsync(Path.Combine(_storage, UpdateLayout.ObjectPath(exe.Sha256)), "kaputt");

        var plan = await _installer.CheckAsync(manifest, verifyAll: false);
        var error = await Assert.ThrowsAsync<UpdateException>(() => _installer.ApplyAsync(plan));

        Assert.Contains("AoS2.exe", error.Message);
        Assert.Equal(3, _source.Requests.Count(r => r == UpdateLayout.ObjectPath(exe.Sha256)));
        Assert.Equal("exe v1", await File.ReadAllTextAsync(GamePath("AoS2.exe")));
        Assert.Equal("0.1.0", (await _installer.LoadStateAsync())!.GameVersion);
    }

    [Fact]
    public async Task TransientError_IsRetried()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        var manifest = await _installer.FetchManifestAsync();
        var plan = await _installer.CheckAsync(manifest, verifyAll: false);

        _source.FailNext(2);
        await _installer.ApplyAsync(plan);

        foreach (var (path, content) in Version1)
            Assert.Equal(content, await File.ReadAllTextAsync(GamePath(path)));
    }

    [Fact]
    public async Task Apply_ReportsCompleteProgress()
    {
        var manifest = await TestRelease.PublishAsync(_storage, "0.1.0", Version1);
        var plan = await _installer.CheckAsync(manifest, verifyAll: false);
        var progress = new SynchronousProgress();

        await _installer.ApplyAsync(plan, progress);

        var last = progress.Reports[^1];
        Assert.Equal(manifest.TotalSize, last.BytesTotal);
        Assert.Equal(manifest.TotalSize, last.BytesDone);
        Assert.Equal(1, last.Fraction);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData(".aos2/install.json")]
    public void GetFullPath_RejectsPathsOutsideGameFolder(string path)
    {
        Assert.Throws<InvalidManifestException>(() => _installer.GetFullPath(path));
    }

    [Fact]
    public async Task FetchManifest_MissingManifest_Throws()
    {
        await Assert.ThrowsAnyAsync<IOException>(() => _installer.FetchManifestAsync());
    }

    private sealed class SynchronousProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];
        public void Report(UpdateProgress value)
        {
            lock (Reports)
                Reports.Add(value);
        }
    }
}
