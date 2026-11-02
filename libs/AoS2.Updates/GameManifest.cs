using System.Text.Json.Serialization;

namespace AoS2.Updates;

/// <summary>
/// Beschreibt eine Spielversion: welche Dateien dazugehören, wie groß sie sind und welche
/// SHA-256-Prüfsumme sie haben. Liegt als <c>game/manifest.json</c> im Update-Speicher.
/// </summary>
public sealed record GameManifest
{
    /// <summary>Format-Version dieses Manifests. Bei inkompatiblen Änderungen erhöhen.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Spielversion, z. B. <c>0.1.0</c> (gleich dem Git-Tag ohne „v“).</summary>
    public required string GameVersion { get; init; }

    /// <summary>Relativer Pfad der Datei, die der Launcher startet, z. B. <c>AoS2.exe</c>.</summary>
    public required string Executable { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }

    /// <summary>Summe aller Dateigrößen in Bytes.</summary>
    [JsonIgnore]
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <param name="Path">Relativer Pfad mit „/“ als Trenner, z. B. <c>AoS2_Data/globalgamemanagers</c>.</param>
/// <param name="Size">Größe in Bytes.</param>
/// <param name="Sha256">SHA-256-Prüfsumme als 64 Hex-Zeichen in Kleinbuchstaben.</param>
public sealed record ManifestFile(string Path, long Size, string Sha256);
