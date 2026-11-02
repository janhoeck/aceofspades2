using System.Text.Json;

namespace AoS2.Updates;

/// <summary>
/// Was der Launcher zuletzt installiert hat. Liegt im Spielordner unter <c>.aos2/install.json</c>.
/// <para>
/// Dank Größe und Änderungszeit muss der Launcher nicht bei jedem Start alle Dateien neu
/// prüfen: Stimmen beide noch, gilt die gespeicherte Prüfsumme. „Dateien prüfen“ rechnet
/// trotzdem alles neu.
/// </para>
/// </summary>
public sealed record InstallState
{
    /// <summary>Reservierter Ordner im Spielordner. Kein Manifest darf hier Dateien ablegen.</summary>
    public const string FolderName = ".aos2";

    public required string GameVersion { get; init; }
    public required string Executable { get; init; }
    public required IReadOnlyList<InstalledFile> Files { get; init; }

    public static string GetFilePath(string installDirectory) =>
        Path.Combine(installDirectory, FolderName, "install.json");

    /// <summary>Liefert <c>null</c>, wenn noch nichts installiert ist oder die Datei kaputt ist.</summary>
    public static async Task<InstallState?> LoadAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        var path = GetFilePath(installDirectory);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, UpdatesJsonContext.Default.InstallState, cancellationToken);
        }
        catch (JsonException)
        {
            // Ohne gültigen Stand prüft der Launcher einfach alle Dateien neu.
            return null;
        }
    }

    public async Task SaveAsync(string installDirectory, CancellationToken cancellationToken = default)
    {
        var path = GetFilePath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Erst in eine Temp-Datei schreiben, dann ersetzen: So bleibt nie eine halbe Datei zurück.
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, this, UpdatesJsonContext.Default.InstallState, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record InstalledFile(string Path, long Size, string Sha256, DateTime LastWriteTimeUtc);
