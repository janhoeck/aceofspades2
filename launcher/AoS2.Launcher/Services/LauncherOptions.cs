using System.Reflection;
using AoS2.Updates;

namespace AoS2.Launcher.Services;

/// <param name="UpdateLocation">Basis des Update-Speichers (URL oder Ordner), <c>null</c> wenn nicht eingestellt.</param>
/// <param name="DataDirectory">Ordner für Einstellungen und den vorgeschlagenen Spielordner.</param>
public sealed record LauncherOptions(string? UpdateLocation, string DataDirectory)
{
    public const string UpdateUrlVariable = "AOS2_UPDATE_URL";
    public const string DataDirVariable = "AOS2_DATA_DIR";

    /// <summary><c>%LocalAppData%\AceOfSpades2</c></summary>
    public static string DefaultDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AceOfSpades2");

    /// <summary>Vorschlag beim ersten Start: <c>%LocalAppData%\AceOfSpades2\Game</c>.</summary>
    public string DefaultGameDirectory => Path.Combine(DataDirectory, "Game");

    public string SettingsPath => Path.Combine(DataDirectory, "launcher.json");

    /// <summary>Velopack-Pakete des Launchers liegen im Unterordner <c>launcher</c>.</summary>
    public string? LauncherUpdateLocation => UpdateLocation is null ? null : Combine(UpdateLocation, UpdateLayout.LauncherFolder);

    /// <summary>
    /// Umgebungsvariablen haben Vorrang (zum Testen), sonst gilt die beim Bauen eingetragene URL.
    /// </summary>
    public static LauncherOptions FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(UpdateUrlVariable),
        typeof(LauncherOptions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AoS2UpdateUrl")?.Value,
        Environment.GetEnvironmentVariable(DataDirVariable));

    public static LauncherOptions Create(string? updateUrlOverride, string? builtInUpdateUrl, string? dataDirOverride)
    {
        var updateLocation = FirstNonEmpty(updateUrlOverride, builtInUpdateUrl)?.TrimEnd('/', '\\');
        var dataDirectory = FirstNonEmpty(dataDirOverride) ?? DefaultDataDirectory;
        return new LauncherOptions(updateLocation, Path.GetFullPath(dataDirectory));
    }

    /// <summary>Version aus dem Git-Tag, ohne angehängten Commit-Hash („0.1.0+abc123“ → „0.1.0“).</summary>
    public static string GetLauncherVersion()
    {
        var version = typeof(LauncherOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    internal static bool IsHttp(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Combine(string location, string child) =>
        IsHttp(location) ? $"{location}/{child}" : Path.Combine(location, child);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
