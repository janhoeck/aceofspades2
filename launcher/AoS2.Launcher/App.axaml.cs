using AoS2.Launcher.Services;
using AoS2.Launcher.ViewModels;
using AoS2.Launcher.Views;
using AoS2.Updates;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AoS2.Launcher;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            window.DataContext = CreateViewModel(new DesktopPlatformServices(() => window));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Hier werden die echten Dienste zusammengesteckt. Die Tests nutzen eigene.</summary>
    private static MainWindowViewModel CreateViewModel(IPlatformServices platform)
    {
        var options = LauncherOptions.FromEnvironment();
        var launcherVersion = LauncherOptions.GetLauncherVersion();

        Func<string, GameInstaller>? createInstaller = null;
        if (options.UpdateLocation is not null)
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AoS2-Launcher/{launcherVersion}");
            var source = UpdateSource.Create(options.UpdateLocation, http);
            createInstaller = directory => new GameInstaller(source, directory);
        }

        IAccountService? accounts = null;
        if (options.SiteUrl is not null && OperatingSystem.IsWindows())
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AoS2-Launcher/{launcherVersion}");
            var store = new SessionStore(options.SessionPath, new DpapiProtector());
            accounts = new AccountService(http, options.SiteUrl, store, platform.OpenUrl);
        }

        return new MainWindowViewModel(
            new VelopackLauncherUpdater(options.LauncherUpdateLocation),
            accounts,
            createInstaller,
            new LauncherSettingsStore(options.SettingsPath),
            options.DefaultGameDirectory,
            new ProcessGameStarter(),
            platform,
            launcherVersion);
    }
}
