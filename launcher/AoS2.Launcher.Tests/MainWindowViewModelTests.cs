using AoS2.Launcher.Services;
using AoS2.Launcher.ViewModels;
using AoS2.Updates;
using AoS2.Updates.Tests;

namespace AoS2.Launcher.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _storage;
    private readonly string _defaultGame;
    private readonly LauncherSettingsStore _settings;
    private readonly FakeLauncherUpdater _launcherUpdater = new();
    private readonly FakeGameStarter _gameStarter = new();
    private readonly FakePlatform _platform = new();

    private static readonly Dictionary<string, string> Files = new()
    {
        ["AoS2.exe"] = "exe",
        ["AoS2_Data/level0"] = "level",
    };

    public MainWindowViewModelTests()
    {
        _storage = _temp.Combine("storage");
        _defaultGame = _temp.Combine("data", "Game");
        _settings = new LauncherSettingsStore(_temp.Combine("data", "launcher.json"));
    }

    public void Dispose() => _temp.Dispose();

    private MainWindowViewModel CreateViewModel(bool withServer = true, Func<IUpdateSource, IUpdateSource>? wrapSource = null)
    {
        IUpdateSource source = new DirectoryUpdateSource(_storage);
        if (wrapSource is not null)
            source = wrapSource(source);

        return new MainWindowViewModel(
            _launcherUpdater,
            withServer ? dir => new GameInstaller(source, dir) { RetryDelay = TimeSpan.Zero } : null,
            _settings,
            _defaultGame,
            _gameStarter,
            _platform,
            "1.0.0");
    }

    /// <summary>Erster Start bis „Bereit“, mit dem vorgeschlagenen Ordner.</summary>
    private async Task<MainWindowViewModel> InstallAsync()
    {
        var vm = CreateViewModel();
        await vm.StartCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        return vm;
    }

    // ---------------------------------------------------------------- Erster Start

    [Fact]
    public async Task FirstStart_AsksForFolder_WithDefaultAndSpaceHint()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(LauncherScreen.ChooseLocation, vm.Screen);
        Assert.Equal(_defaultGame, vm.GameDirectory);
        Assert.StartsWith("Braucht 0,0 MB. Auf ", vm.SpaceHint);
        Assert.False(Directory.Exists(_defaultGame), "Vor dem Klick auf „Installieren“ wird nichts geschrieben.");
        Assert.False(vm.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task FirstStart_ChooseFolder_AppendsGameFolder()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = CreateViewModel();
        await vm.StartCommand.ExecuteAsync(null);
        var spiele = _temp.Combine("Spiele");
        _platform.NextFolder = spiele;

        await vm.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(spiele, "Ace of Spades 2"), vm.GameDirectory);
    }

    [Fact]
    public async Task FirstStart_ChooseFolderCancelled_KeepsFolder()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = CreateViewModel();
        await vm.StartCommand.ExecuteAsync(null);

        await vm.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(_defaultGame, vm.GameDirectory);
    }

    [Fact]
    public async Task Install_InstallsIntoChosenFolder_AndRemembersIt()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = CreateViewModel();
        await vm.StartCommand.ExecuteAsync(null);
        _platform.NextFolder = _temp.Combine("Spiele");
        await vm.ChooseFolderCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        var game = _temp.Combine("Spiele", "Ace of Spades 2");
        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        Assert.Equal("0.1.0", vm.InstalledVersion);
        Assert.Equal(Path.Combine(game, "AoS2.exe"), vm.ExecutablePath);
        Assert.Equal(game, _settings.Load().GameDirectory);
        Assert.True(vm.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task Install_NotEnoughSpace_CannotInstall()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        _platform.FreeBytes = 1;
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasSpaceProblem);
        Assert.StartsWith("Nicht genug Platz", vm.SpaceHint);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task SecondStart_UsesSavedFolder_WithoutAsking()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        await InstallAsync();
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        Assert.Equal(_defaultGame, vm.GameDirectory);
    }

    [Fact]
    public async Task Start_InstallFromOlderLauncher_IsAdopted()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var old = new GameInstaller(new DirectoryUpdateSource(_storage), _defaultGame);
        await old.ApplyAsync(await old.CheckAsync(await old.FetchManifestAsync(), verifyAll: false));
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        Assert.Equal(_defaultGame, _settings.Load().GameDirectory);
    }

    // ---------------------------------------------------------------- Spielen

    [Fact]
    public async Task Play_StartsGame_AndClosesLauncher()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.PlayCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(Path.Combine(_defaultGame, "AoS2.exe"), _gameStarter.StartedPath);
    }

    [Fact]
    public async Task Play_CloseOnGameStartOff_KeepsLauncherOpen_AndIsRemembered()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CloseOnGameStart = false;
        vm.PlayCommand.Execute(null);

        Assert.False(closed);
        Assert.NotNull(_gameStarter.StartedPath);
        Assert.False(_settings.Load().CloseOnGameStart);
        Assert.Equal(_defaultGame, _settings.Load().GameDirectory);
    }

    // ---------------------------------------------------------------- Launcher-Update und Fehler

    [Fact]
    public async Task Start_LauncherUpdateFound_DoesNotTouchGame()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        _launcherUpdater.Result = true;
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Launcher wird neu gestartet …", vm.StatusText);
        Assert.Equal(LauncherScreen.Working, vm.Screen);
    }

    [Fact]
    public async Task Start_LauncherUpdateFails_StillOffersInstall()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        _launcherUpdater.Error = new HttpRequestException("kaputt");
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(LauncherScreen.ChooseLocation, vm.Screen);
    }

    [Fact]
    public async Task Start_NoUpdateUrl_ShowsProblem()
    {
        var vm = CreateViewModel(withServer: false);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasProblem);
        Assert.Contains(LauncherOptions.UpdateUrlVariable, vm.HintText);
        Assert.False(vm.PlayCommand.CanExecute(null));
        Assert.False(vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task FirstStart_ServerUnreachable_ShowsProblem_RetryWorks()
    {
        var vm = CreateViewModel();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.HasProblem);
        Assert.False(vm.CanPlayNow);

        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(LauncherScreen.ChooseLocation, vm.Screen);
    }

    [Fact]
    public async Task Start_ServerUnreachable_PreviousInstall_CanPlayOffline()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        await InstallAsync();
        Directory.Delete(_storage, recursive: true);
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasProblem);
        Assert.Equal("Kein Update möglich", vm.StatusText);
        Assert.Contains("0.1.0 trotzdem spielen", vm.DetailText);
        Assert.True(vm.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_DownloadFailsMidUpdate_CannotPlayHalfUpdatedGame()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        await InstallAsync();
        var manifest = await TestRelease.PublishAsync(_storage, "0.2.0", new Dictionary<string, string> { ["AoS2.exe"] = "exe v2", ["AoS2_Data/level0"] = "level" });
        File.Delete(Path.Combine(_storage, UpdateLayout.ObjectPath(manifest.Files[0].Sha256)));
        var vm = CreateViewModel();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasProblem);
        Assert.Equal("Update abgebrochen", vm.StatusText);
        Assert.False(vm.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_DuringDownload_StopsAndAllowsRetry()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var gate = new GateSource();
        var vm = CreateViewModel(wrapSource: inner => gate.Wrap(inner));
        await vm.StartCommand.ExecuteAsync(null);

        var install = vm.InstallCommand.ExecuteAsync(null);
        await gate.ObjectRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await install.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(vm.HasProblem);
        Assert.Equal("Installation abgebrochen", vm.StatusText);
        Assert.False(vm.CanCancel);
        Assert.False(vm.PlayCommand.CanExecute(null));

        gate.Open = true;
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.Equal(LauncherScreen.Ready, vm.Screen);
    }

    // ---------------------------------------------------------------- Einstellungen

    [Fact]
    public async Task Verify_RepairsDamagedFile()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();
        var level = Path.Combine(_defaultGame, "AoS2_Data", "level0");
        var timestamp = File.GetLastWriteTimeUtc(level);
        await File.WriteAllTextAsync(level, "LEVEL");
        File.SetLastWriteTimeUtc(level, timestamp);
        vm.OpenSettingsCommand.Execute(null);

        await vm.VerifyCommand.ExecuteAsync(null);

        Assert.False(vm.IsSettingsOpen);
        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        Assert.Equal("level", await File.ReadAllTextAsync(level));
    }

    [Fact]
    public async Task MoveGame_MovesInstallation_AndRemembersFolder()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();
        _platform.NextFolder = _temp.Combine("Neu");

        await vm.MoveGameCommand.ExecuteAsync(null);

        var target = _temp.Combine("Neu", "Ace of Spades 2");
        Assert.Equal(LauncherScreen.Ready, vm.Screen);
        Assert.Equal(target, vm.GameDirectory);
        Assert.Equal(target, _settings.Load().GameDirectory);
        Assert.Equal(Path.Combine(target, "AoS2.exe"), vm.ExecutablePath);
        Assert.False(File.Exists(Path.Combine(_defaultGame, "AoS2.exe")));
    }

    [Fact]
    public async Task MoveGame_TargetNotEmpty_ShowsProblem_GameStillPlayable()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();
        var target = _temp.Combine("Belegt", "Ace of Spades 2");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "x.txt"), "x");
        _platform.NextFolder = target;

        await vm.MoveGameCommand.ExecuteAsync(null);

        Assert.True(vm.HasProblem);
        Assert.Equal("Verschieben nicht möglich", vm.StatusText);
        Assert.Equal(_defaultGame, vm.GameDirectory);
        Assert.True(vm.PlayCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenGameFolder_OpensCurrentFolder()
    {
        await TestRelease.PublishAsync(_storage, "0.1.0", Files);
        var vm = await InstallAsync();

        vm.OpenGameFolderCommand.Execute(null);

        Assert.Equal(_defaultGame, _platform.OpenedFolder);
    }

    // ---------------------------------------------------------------- Hilfen

    [Theory]
    [InlineData(0, "0,0 MB")]
    [InlineData(103_284_736, "98,5 MB")]
    [InlineData(2_147_483_648, "2,0 GB")]
    [InlineData(442_381_631_488, "412 GB")]
    public void FormatBytes(long bytes, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.FormatBytes(bytes));
    }

    [Fact]
    public void Describe_NetworkError_MentionsServer()
    {
        Assert.Equal("Der Update-Server antwortet nicht.", MainWindowViewModel.Describe(new HttpRequestException("timeout")));
    }

    private sealed class FakeLauncherUpdater : ILauncherUpdater
    {
        public bool Result { get; set; }
        public Exception? Error { get; set; }

        public Task<bool> UpdateAndRestartAsync(IProgress<int> progress, CancellationToken cancellationToken) =>
            Error is null ? Task.FromResult(Result) : Task.FromException<bool>(Error);
    }

    private sealed class FakeGameStarter : IGameStarter
    {
        public string? StartedPath { get; private set; }
        public void Start(string executablePath) => StartedPath = executablePath;
    }

    private sealed class FakePlatform : IPlatformServices
    {
        public string? NextFolder { get; set; }
        public long? FreeBytes { get; set; } = 500L * 1024 * 1024 * 1024;
        public string? OpenedFolder { get; private set; }

        public Task<string?> PickFolderAsync(string title, string? startDirectory) => Task.FromResult(NextFolder);
        public void OpenFolder(string path) => OpenedFolder = path;
        public long? GetAvailableBytes(string path) => FreeBytes;
    }

    /// <summary>Hält Datei-Downloads an, bis <see cref="Open"/> gesetzt ist oder abgebrochen wird.</summary>
    private sealed class GateSource
    {
        public TaskCompletionSource ObjectRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Open { get; set; }

        public IUpdateSource Wrap(IUpdateSource inner) => new Gated(this, inner);

        private sealed class Gated(GateSource gate, IUpdateSource inner) : IUpdateSource
        {
            public string Location => inner.Location;

            public async Task<Stream> OpenReadAsync(string relativePath, bool bypassCache, CancellationToken cancellationToken)
            {
                if (relativePath.StartsWith("game/objects/") && !gate.Open)
                {
                    gate.ObjectRequested.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return await inner.OpenReadAsync(relativePath, bypassCache, cancellationToken);
            }
        }
    }
}
