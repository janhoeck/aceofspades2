using System.Text.RegularExpressions;

namespace AoS2.Updates;

/// <summary>
/// Prüft ein Manifest, bevor der Launcher ihm vertraut. Wichtig vor allem für die Pfade:
/// Ein Pfad wie <c>../../Windows/x.dll</c> darf nie außerhalb des Spielordners landen.
/// </summary>
public static partial class ManifestValidator
{
    /// <summary>Wirft <see cref="InvalidManifestException"/>, wenn das Manifest ungültig ist.</summary>
    public static void Validate(GameManifest manifest)
    {
        if (manifest.FormatVersion != GameManifest.CurrentFormatVersion)
        {
            throw new InvalidManifestException(
                $"Manifest-Format {manifest.FormatVersion} wird nicht unterstützt (erwartet {GameManifest.CurrentFormatVersion}).");
        }

        if (!IsValidVersion(manifest.GameVersion))
            throw new InvalidManifestException($"Ungültige Spielversion „{manifest.GameVersion}“.");

        if (manifest.Files.Count == 0)
            throw new InvalidManifestException("Das Manifest enthält keine Dateien.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!IsValidPath(file.Path))
                throw new InvalidManifestException($"Ungültiger Dateipfad „{file.Path}“.");
            if (!seen.Add(file.Path))
                throw new InvalidManifestException($"Der Pfad „{file.Path}“ kommt mehrfach vor.");
            if (file.Size < 0)
                throw new InvalidManifestException($"Negative Größe bei „{file.Path}“.");
            if (!IsValidSha256(file.Sha256))
                throw new InvalidManifestException($"Ungültige Prüfsumme bei „{file.Path}“.");
        }

        if (!seen.Contains(manifest.Executable))
            throw new InvalidManifestException($"Die Startdatei „{manifest.Executable}“ fehlt in der Dateiliste.");
    }

    /// <summary>
    /// Erlaubt sind nur relative Pfade mit „/“ als Trenner, ohne „.“- oder „..“-Teile,
    /// ohne Laufwerksbuchstaben und außerhalb des reservierten Ordners <c>.aos2</c>.
    /// </summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':'))
            return false;

        var segments = path.Split('/');
        if (segments[0].Equals(InstallState.FolderName, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
            // Windows erlaubt keine Namen, die auf Leerzeichen oder Punkt enden.
            if (segment.IndexOfAny(InvalidNameChars) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.'))
                return false;
        }

        return true;
    }

    public static bool IsValidSha256(string? sha256) => sha256 is not null && Sha256Regex().IsMatch(sha256);

    /// <summary>SemVer-Form <c>1.2.3</c> oder <c>1.2.3-beta.1</c>.</summary>
    public static bool IsValidVersion(string? version) => version is not null && VersionRegex().IsMatch(version);

    // Feste Liste statt Path.GetInvalidFileNameChars(): Die liefert unter Linux (CI) weniger Zeichen.
    private static readonly char[] InvalidNameChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*', .. Enumerable.Range(0, 32).Select(i => (char)i)];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex VersionRegex();
}

public sealed class InvalidManifestException(string message) : Exception(message);
