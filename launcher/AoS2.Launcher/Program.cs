using AoS2.Launcher.Services;
using Avalonia;
using Velopack;

namespace AoS2.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Muss als Allererstes laufen: Velopack startet die exe bei Installation, Update und
        // Deinstallation mit eigenen Argumenten, erledigt seine Arbeit und beendet sie sofort wieder.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
            velopack.OnBeforeUninstallFastCallback(_ => LauncherUninstaller.Run());
        velopack.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Wird auch vom Avalonia-Designer in der IDE benutzt.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
