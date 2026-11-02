using Velopack;
using Velopack.Sources;

namespace AoS2.Launcher.Services;

/// <summary>Aktualisiert den Launcher selbst.</summary>
public interface ILauncherUpdater
{
    /// <summary>
    /// Sucht ein neues Launcher-Release. Gibt es eins, wird es geladen, installiert und der Launcher
    /// neu gestartet (der Prozess endet dabei). Gibt <c>false</c> zurück, wenn nichts zu tun ist.
    /// </summary>
    /// <param name="progress">Download-Fortschritt in Prozent.</param>
    Task<bool> UpdateAndRestartAsync(IProgress<int> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Self-Update mit Velopack. Velopack liest <c>releases.win.json</c> aus dem Ordner <c>launcher/</c>
/// im Update-Speicher, lädt bei Bedarf ein Delta- oder Vollpaket und tauscht den Launcher aus.
/// </summary>
public sealed class VelopackLauncherUpdater(string? location) : ILauncherUpdater
{
    public async Task<bool> UpdateAndRestartAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (location is null)
            return false;

        IUpdateSource source = LauncherOptions.IsHttp(location)
            ? new SimpleWebSource(location)
            : new SimpleFileSource(new DirectoryInfo(location));
        var manager = new UpdateManager(source);

        // Beim Start aus der IDE oder per „dotnet run“ ist der Launcher nicht installiert.
        if (!manager.IsInstalled)
            return false;

        var update = await manager.CheckForUpdatesAsync();
        if (update is null)
            return false;

        await manager.DownloadUpdatesAsync(update, progress.Report, cancellationToken);
        manager.ApplyUpdatesAndRestart(update);
        return true;
    }
}
