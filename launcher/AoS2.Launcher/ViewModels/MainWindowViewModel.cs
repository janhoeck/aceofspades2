using System.Globalization;
using AoS2.Launcher.Services;
using AoS2.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AoS2.Launcher.ViewModels;

/// <summary>Welcher untere Bereich gerade zu sehen ist.</summary>
public enum LauncherScreen
{
    /// <summary>Prüfen, Laden, Verschieben: mit Fortschrittsbalken.</summary>
    Working,

    /// <summary>Erster Start: Spielordner wählen.</summary>
    ChooseLocation,

    Ready,

    /// <summary>Fehler. Ist noch eine vollständige Version installiert, kann man sie trotzdem spielen.</summary>
    Problem,
}

/// <summary>
/// Zustand und Abläufe des Launcher-Fensters. Kennt keine Avalonia-Typen, damit es ohne Fenster testbar ist.
/// <para>
/// Ablauf: Launcher-Update → (erster Start: Spielordner wählen) → Manifest laden → Dateien vergleichen
/// → fehlende laden → „Spielen“.
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly ILauncherUpdater _launcherUpdater;
    private readonly Func<string, GameInstaller>? _createInstaller;
    private readonly LauncherSettingsStore _settings;
    private readonly string _defaultGameDirectory;
    private readonly IGameStarter _gameStarter;
    private readonly IPlatformServices _platform;
    private CancellationTokenSource? _downloadCancellation;
    private long _requiredBytes;

    /// <param name="createInstaller">Erzeugt den Installer für einen Spielordner; <c>null</c>, wenn kein Update-Server eingestellt ist.</param>
    public MainWindowViewModel(
        ILauncherUpdater launcherUpdater,
        Func<string, GameInstaller>? createInstaller,
        LauncherSettingsStore settings,
        string defaultGameDirectory,
        IGameStarter gameStarter,
        IPlatformServices platform,
        string launcherVersion)
    {
        _launcherUpdater = launcherUpdater;
        _createInstaller = createInstaller;
        _settings = settings;
        _defaultGameDirectory = defaultGameDirectory;
        _gameStarter = gameStarter;
        _platform = platform;
        LauncherVersion = launcherVersion;
        GameDirectory = defaultGameDirectory;
    }

    /// <summary>Das Spiel läuft und der Launcher soll sich schließen.</summary>
    public event EventHandler? CloseRequested;

    public string LauncherVersion { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(IsChoosingLocation), nameof(IsReady), nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(VerifyCommand), nameof(RetryCommand), nameof(MoveGameCommand), nameof(InstallCommand), nameof(ChooseFolderCommand))]
    public partial LauncherScreen Screen { get; set; } = LauncherScreen.Working;

    public bool IsWorking => Screen == LauncherScreen.Working;
    public bool IsChoosingLocation => Screen == LauncherScreen.ChooseLocation;
    public bool IsReady => Screen == LauncherScreen.Ready;
    public bool HasProblem => Screen == LauncherScreen.Problem;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Starte …";

    [ObservableProperty]
    public partial string? DetailText { get; set; }

    /// <summary>Kleiner grauer Zusatz, z. B. die technische Fehlermeldung.</summary>
    [ObservableProperty]
    public partial string? HintText { get; set; }

    /// <summary>0 bis 100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    /// <summary>z. B. „42,1 von 98,5 MB“.</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; set; }

    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    /// <summary>„Braucht 98,5 MB. Auf D: sind 412 GB frei.“</summary>
    [ObservableProperty]
    public partial string? SpaceHint { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool HasSpaceProblem { get; set; }

    [ObservableProperty]
    public partial string? InstalledVersion { get; set; }

    /// <summary>Voller Pfad der Spiel-exe. <c>null</c>, solange nichts Spielbares installiert ist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayNow))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial string? ExecutablePath { get; set; }

    /// <summary>Für die Sichtbarkeit des „Spielen“-Buttons bei Problemen.</summary>
    public bool CanPlayNow => ExecutablePath is not null;

    [ObservableProperty]
    public partial bool CloseOnGameStart { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    partial void OnCloseOnGameStartChanged(bool value) => _settings.Save(_settings.Load() with { CloseOnGameStart = value });

    // ---------------------------------------------------------------- Start

    /// <summary>Wird aufgerufen, sobald das Fenster offen ist.</summary>
    [RelayCommand]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        ShowWorking("Suche nach Launcher-Updates …");
        try
        {
            var progress = new Progress<int>(percent =>
            {
                StatusText = "Lade neuen Launcher …";
                IsProgressIndeterminate = false;
                Progress = percent;
            });

            if (await _launcherUpdater.UpdateAndRestartAsync(progress, cancellationToken))
            {
                StatusText = "Launcher wird neu gestartet …";
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ein kaputtes Launcher-Update soll das Spielen nicht verhindern.
            System.Diagnostics.Trace.TraceWarning($"Launcher-Update fehlgeschlagen: {ex}");
        }

        await PrepareGameAsync(cancellationToken);
    }

    private async Task PrepareGameAsync(CancellationToken cancellationToken)
    {
        if (_createInstaller is null)
        {
            ShowProblem("Kein Update-Server eingestellt",
                "Dieser Launcher wurde ohne Update-Adresse gebaut.",
                $"Zum Testen die Umgebungsvariable {LauncherOptions.UpdateUrlVariable} setzen.");
            return;
        }

        var settings = _settings.Load();
        CloseOnGameStart = settings.CloseOnGameStart;

        var directory = settings.GameDirectory;
        if (directory is null && Installation.IsInstalled(_defaultGameDirectory))
        {
            // Installation aus einer Launcher-Version ohne Ordnerwahl übernehmen.
            directory = _defaultGameDirectory;
            _settings.Save(settings with { GameDirectory = directory });
        }

        if (directory is null)
        {
            await OfferLocationAsync(cancellationToken);
            return;
        }

        GameDirectory = directory;
        await UpdateGameAsync(verifyAll: false, cancellationToken);
    }

    // ---------------------------------------------------------------- Erster Start: Ordner wählen

    private async Task OfferLocationAsync(CancellationToken cancellationToken)
    {
        ShowWorking("Suche nach dem Spiel …");
        try
        {
            var manifest = await _createInstaller!(GameDirectory).FetchManifestAsync(cancellationToken);
            _requiredBytes = manifest.TotalSize;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ShowProblem("Spiel konnte nicht installiert werden", Describe(ex), ex.Message);
            return;
        }

        StatusText = "Wohin soll das Spiel installiert werden?";
        UpdateSpaceHint();
        Screen = LauncherScreen.ChooseLocation;
    }

    [RelayCommand(CanExecute = nameof(IsChoosingLocation))]
    private async Task ChooseFolderAsync()
    {
        var picked = await _platform.PickFolderAsync("Ordner für Ace of Spades 2 wählen", GameDirectory);
        if (picked is null)
            return;

        GameDirectory = Installation.ResolveGameDirectory(picked);
        UpdateSpaceHint();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        _settings.Save(_settings.Load() with { GameDirectory = GameDirectory });
        await UpdateGameAsync(verifyAll: false, cancellationToken);
    }

    private bool CanInstall() => IsChoosingLocation && !HasSpaceProblem;

    private void UpdateSpaceHint()
    {
        var required = FormatBytes(_requiredBytes);
        var free = _platform.GetAvailableBytes(GameDirectory);
        var drive = Path.GetPathRoot(GameDirectory)?.TrimEnd('\\', '/');

        HasSpaceProblem = free is not null && free < _requiredBytes;
        SpaceHint = free is null
            ? $"Braucht {required}."
            : HasSpaceProblem
                ? $"Nicht genug Platz: Das Spiel braucht {required}, auf {drive} sind nur {FormatBytes(free.Value)} frei."
                : $"Braucht {required}. Auf {drive} sind {FormatBytes(free.Value)} frei. Der Ordner lässt sich später in den Einstellungen ändern.";
    }

    // ---------------------------------------------------------------- Installieren / Aktualisieren

    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private Task VerifyAsync(CancellationToken cancellationToken)
    {
        IsSettingsOpen = false;
        return UpdateGameAsync(verifyAll: true, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync(CancellationToken cancellationToken) =>
        _settings.Load().GameDirectory is null
            ? PrepareGameAsync(cancellationToken)
            : UpdateGameAsync(verifyAll: false, cancellationToken);

    private bool CanRunUpdate() => !IsWorking && !IsChoosingLocation && _createInstaller is not null;

    private bool CanRetry() => HasProblem && _createInstaller is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _downloadCancellation?.Cancel();

    private async Task UpdateGameAsync(bool verifyAll, CancellationToken cancellationToken)
    {
        var installer = _createInstaller!(GameDirectory);
        var applying = false;
        InstallState? state = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _downloadCancellation = cancellation;
        try
        {
            ShowWorking("Suche nach Spiel-Updates …");
            state = await installer.LoadStateAsync(cancellation.Token);
            InstalledVersion = state?.GameVersion;
            var manifest = await installer.FetchManifestAsync(cancellation.Token);

            StatusText = verifyAll ? "Prüfe alle Dateien …" : "Vergleiche Dateien …";
            IsProgressIndeterminate = false;
            var checkProgress = new Progress<UpdateProgress>(p => Progress = p.Fraction * 100);
            var plan = await installer.CheckAsync(manifest, verifyAll, checkProgress, cancellation.Token);

            if (plan.Downloads.Count > 0)
            {
                StatusText = state is null
                    ? $"Installiere Version {manifest.GameVersion}"
                    : $"Lade Update auf Version {manifest.GameVersion}";
                DetailText = state is null ? $"Nach {GameDirectory}" : "Nur geänderte Dateien werden geladen.";
                Progress = 0;
                ProgressText = $"0 von {FormatBytes(plan.DownloadSize)}";
                CanCancel = true;
            }
            var downloadProgress = new Progress<UpdateProgress>(p =>
            {
                Progress = p.Fraction * 100;
                ProgressText = $"{FormatBytes(p.BytesDone)} von {FormatBytes(p.BytesTotal)}";
            });

            // Ab hier können Dateien schon ersetzt sein. Scheitert es jetzt, ist die alte Version nicht mehr sicher spielbar.
            applying = true;
            ExecutablePath = null;
            await installer.ApplyAsync(plan, downloadProgress, cancellation.Token);

            InstalledVersion = manifest.GameVersion;
            ExecutablePath = installer.GetFullPath(manifest.Executable);
            ShowReady();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ShowProblem(state is null ? "Installation abgebrochen" : "Update abgebrochen",
                "Bereits geladene Dateien bleiben erhalten.",
                "„Erneut versuchen“ lädt den Rest.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Auch Zeitüberschreitungen landen hier (HttpClient meldet sie als TaskCanceledException).
            HandleUpdateError(installer, ex, applying, state);
        }
        finally
        {
            CanCancel = false;
            _downloadCancellation = null;
        }
    }

    /// <param name="applying">Ob schon Dateien ersetzt wurden.</param>
    /// <param name="state">Stand der zuvor installierten Version.</param>
    private void HandleUpdateError(GameInstaller installer, Exception error, bool applying, InstallState? state)
    {
        if (applying)
        {
            ShowProblem("Update abgebrochen", Describe(error), error.Message);
            return;
        }

        // Ohne Verbindung zum Update-Server darf man die zuletzt vollständig installierte Version spielen.
        if (state is not null && ManifestValidator.IsValidPath(state.Executable))
        {
            var executable = installer.GetFullPath(state.Executable);
            if (File.Exists(executable))
            {
                ExecutablePath = executable;
                ShowProblem("Kein Update möglich", $"{Describe(error)} Du kannst Version {state.GameVersion} trotzdem spielen.",
                    "Online-Server erwarten eventuell eine neuere Version.");
                return;
            }
        }

        ShowProblem("Spiel konnte nicht installiert werden", Describe(error), error.Message);
    }

    // ---------------------------------------------------------------- Spielen

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        try
        {
            _gameStarter.Start(ExecutablePath!);
            if (CloseOnGameStart)
                CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            ShowProblem("Das Spiel konnte nicht gestartet werden", "Windows hat den Start verweigert.", ex.Message);
        }
    }

    private bool CanPlay() => !IsWorking && ExecutablePath is not null;

    // ---------------------------------------------------------------- Einstellungen

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void OpenGameFolder() => _platform.OpenFolder(GameDirectory);

    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private async Task MoveGameAsync(CancellationToken cancellationToken)
    {
        var picked = await _platform.PickFolderAsync("Neuen Ordner für Ace of Spades 2 wählen", GameDirectory);
        if (picked is null)
            return;

        var target = Installation.ResolveGameDirectory(picked);
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(GameDirectory), StringComparison.OrdinalIgnoreCase))
            return;

        IsSettingsOpen = false;
        ShowWorking("Verschiebe das Spiel …");
        DetailText = $"Nach {target}";
        IsProgressIndeterminate = false;
        try
        {
            if (Installation.IsInstalled(GameDirectory))
            {
                var progress = new Progress<UpdateProgress>(p => Progress = p.Fraction * 100);
                await Installation.MoveAsync(GameDirectory, target, progress, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Die alte Installation ist unverändert.
            ShowProblem("Verschieben nicht möglich", Describe(ex), "Das Spiel liegt weiter im alten Ordner.");
            return;
        }

        GameDirectory = target;
        _settings.Save(_settings.Load() with { GameDirectory = target });
        await UpdateGameAsync(verifyAll: false, cancellationToken);
    }

    // ---------------------------------------------------------------- Hilfen

    private void ShowWorking(string status)
    {
        Screen = LauncherScreen.Working;
        StatusText = status;
        DetailText = null;
        HintText = null;
        ProgressText = null;
        IsProgressIndeterminate = true;
        Progress = 0;
    }

    private void ShowReady()
    {
        Screen = LauncherScreen.Ready;
        StatusText = "Bereit";
        DetailText = $"Version {InstalledVersion} ist installiert";
        HintText = null;
        Progress = 100;
    }

    private void ShowProblem(string status, string detail, string? hint)
    {
        Screen = LauncherScreen.Problem;
        StatusText = status;
        DetailText = detail;
        HintText = hint;
        IsProgressIndeterminate = false;
        Progress = 0;
    }

    internal static string Describe(Exception error) => error switch
    {
        HttpRequestException => "Der Update-Server antwortet nicht.",
        InvalidManifestException => "Die Daten auf dem Update-Server sind ungültig.",
        UpdateException update => update.Message,
        FileNotFoundException or DirectoryNotFoundException => "Im Update-Speicher fehlt eine Datei.",
        UnauthorizedAccessException => "Windows verweigert den Zugriff auf den Spielordner.",
        IOException => "Eine Datei ist gesperrt. Läuft das Spiel noch?",
        _ => error.Message,
    };

    internal static string FormatBytes(long bytes)
    {
        const double MB = 1024 * 1024, GB = MB * 1024;
        return bytes >= 10 * GB ? (bytes / GB).ToString("0", German) + " GB"
            : bytes >= GB ? (bytes / GB).ToString("0.0", German) + " GB"
            : (bytes / MB).ToString("0.0", German) + " MB";
    }
}
