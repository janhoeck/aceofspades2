using System.Text.Json;
using System.Text.Json.Serialization;

namespace AoS2.Launcher.Services;

/// <summary>Was sich der Launcher zwischen zwei Starts merkt. Liegt als <c>launcher.json</c> im Datenordner.</summary>
public sealed record LauncherSettings
{
    /// <summary>Spielordner. <c>null</c>, solange beim ersten Start noch keiner gewählt wurde.</summary>
    public string? GameDirectory { get; init; }

    public bool CloseOnGameStart { get; init; } = true;
}

public sealed class LauncherSettingsStore(string path)
{
    public string Path { get; } = path;

    /// <summary>Fehlt die Datei oder ist sie kaputt, gelten die Standardwerte.</summary>
    public LauncherSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new LauncherSettings();
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize(stream, LauncherJsonContext.Default.LauncherSettings) ?? new LauncherSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, LauncherJsonContext.Default.LauncherSettings);
        }
        File.Move(temp, Path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJsonContext : JsonSerializerContext;
