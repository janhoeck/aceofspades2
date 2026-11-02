using AoS2.Updates;

namespace AoS2.Launcher.Services;

/// <summary>
/// Läuft, wenn jemand den Launcher über Windows deinstalliert (Velopack-Hook).
/// Entfernt das Spiel, aber nur die Dateien, die der Launcher selbst installiert hat.
/// </summary>
public static class LauncherUninstaller
{
    public static void Run()
    {
        try
        {
            // Absichtlich ohne Umgebungsvariablen: Bei der Deinstallation gilt nur der echte Datenordner.
            var options = LauncherOptions.Create(null, null, null);
            var store = new LauncherSettingsStore(options.SettingsPath);
            var gameDirectory = store.Load().GameDirectory ?? options.DefaultGameDirectory;

            Installation.RemoveAsync(gameDirectory).GetAwaiter().GetResult();
            store.Delete();
            File.Delete(options.SessionPath);
            Installation.DeleteEmptyDirectories(options.DataDirectory, includeRoot: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deinstallation nicht blockieren. Reste kann man von Hand löschen.
        }
    }
}
