using System.Reflection;
using AoS2.Contracts;
using AoS2.Updates;

namespace AoS2.Launcher.Services;

/// <param name="UpdateLocation">Basis des Update-Speichers (URL oder Ordner), <c>null</c> wenn nicht eingestellt.</param>
/// <param name="DataDirectory">Ordner für Einstellungen, Anmeldung und den vorgeschlagenen Spielordner.</param>
/// <param name="SiteUrl">Adresse der Website (Discord-Login, API unter <c>/api</c>), <c>null</c> wenn nicht eingestellt.</param>
public sealed record LauncherOptions(string? UpdateLocation, string DataDirectory, string? SiteUrl = null)
{
    public const string UpdateUrlVariable = "AOS2_UPDATE_URL";
    public const string DataDirVariable = SavedSession.DataDirVariable;
    public const string SiteUrlVariable = LaunchEnvironment.SiteUrl;

    /// <summary><c>%LocalAppData%\AceOfSpades2</c></summary>
    public static string DefaultDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SavedSession.DataFolderName);

    /// <summary>Vorschlag beim ersten Start: <c>%LocalAppData%\AceOfSpades2\Game</c>.</summary>
    public string DefaultGameDirectory => Path.Combine(DataDirectory, "Game");

    public string SettingsPath => Path.Combine(DataDirectory, "launcher.json");

    /// <summary>Gespeicherte Anmeldung (verschlüsselt).</summary>
    public string SessionPath => Path.Combine(DataDirectory, SavedSession.FileName);

    /// <summary>Velopack-Pakete des Launchers liegen im Unterordner <c>launcher</c>.</summary>
    public string? LauncherUpdateLocation => UpdateLocation is null ? null : Combine(UpdateLocation, UpdateLayout.LauncherFolder);

    /// <summary>
    /// Umgebungsvariablen haben Vorrang (zum Testen), sonst gelten die beim Bauen eingetragenen Adressen.
    /// </summary>
    public static LauncherOptions FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(UpdateUrlVariable),
        GetBuiltInValue("AoS2UpdateUrl"),
        Environment.GetEnvironmentVariable(DataDirVariable),
        Environment.GetEnvironmentVariable(SiteUrlVariable),
        GetBuiltInValue("AoS2SiteUrl"));

    public static LauncherOptions Create(
        string? updateUrlOverride,
        string? builtInUpdateUrl,
        string? dataDirOverride,
        string? siteUrlOverride = null,
        string? builtInSiteUrl = null)
    {
        var updateLocation = FirstNonEmpty(updateUrlOverride, builtInUpdateUrl)?.TrimEnd('/', '\\');
        var dataDirectory = FirstNonEmpty(dataDirOverride) ?? DefaultDataDirectory;
        var siteUrl = FirstNonEmpty(siteUrlOverride, builtInSiteUrl)?.TrimEnd('/');
        return new LauncherOptions(updateLocation, Path.GetFullPath(dataDirectory), siteUrl);
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

    private static string? GetBuiltInValue(string key) =>
        typeof(LauncherOptions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    private static string Combine(string location, string child) =>
        IsHttp(location) ? $"{location}/{child}" : Path.Combine(location, child);

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
