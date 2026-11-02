using AoS2.Updates;

namespace AoS2.ManifestBuilder;

/// <param name="InputDirectory">Ordner mit dem fertigen Unity-Build.</param>
/// <param name="OutputDirectory">Release-Ordner. Darin entsteht <c>game/…</c> wie im Update-Speicher.</param>
/// <param name="Excludes">Glob-Muster für Dateien, die nicht ausgeliefert werden.</param>
public sealed record BuilderOptions(
    string InputDirectory,
    string OutputDirectory,
    string GameVersion,
    string Executable,
    IReadOnlyList<string> Excludes)
{
    public const string DefaultExecutable = "AoS2.exe";

    /// <summary>
    /// Ordner, die Unity neben den Build legt, die aber nie zu Spielern gehören, z. B.
    /// <c>AoS2_BurstDebugInformation_DoNotShip</c> oder <c>AoS2_BackUpThisFolder_ButDontShipItWithYourGame</c>
    /// (der genaue Name hängt von der Unity-Version ab).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludes =
    [
        "**/*_DoNotShip/**",
        "**/*_ButDontShip*/**",
    ];

    public const string Usage = """
        AoS2.ManifestBuilder – erzeugt das Update-Manifest aus einem Unity-Build.

        Aufruf:
          AoS2.ManifestBuilder --input <ordner> --output <ordner> --version <x.y.z> [Optionen]

        Optionen:
          --input <ordner>       Unity-Build (enthält AoS2.exe)
          --output <ordner>      Release-Ordner, z. B. artifacts/release
          --version <x.y.z>      Spielversion, z. B. 0.1.0 (ein führendes „v“ wird entfernt)
          --executable <pfad>    Startdatei relativ zum Build (Standard: AoS2.exe)
          --exclude <muster>     Weitere Dateien auslassen (Glob, mehrfach möglich)
          --help                 Diese Hilfe

        Ergebnis im Release-Ordner:
          game/manifest.json, game/manifests/<version>.json, game/objects/…
        """;

    /// <summary>Liest die Kommandozeile. Bei Fehlern steht in <paramref name="error"/> der Grund.</summary>
    public static BuilderOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? input = null, output = null, version = null, executable = null;
        var excludes = new List<string>(DefaultExcludes);
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h" or "/?")
            {
                error = "";
                return null;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Für „{name}“ fehlt ein Wert.";
                return null;
            }

            var value = args[++i];
            switch (name)
            {
                case "--input": input = value; break;
                case "--output": output = value; break;
                case "--version": version = value; break;
                case "--executable": executable = value; break;
                case "--exclude": excludes.Add(value); break;
                default:
                    error = $"Unbekannte Option „{name}“.";
                    return null;
            }
        }

        if (input is null || output is null || version is null)
        {
            error = "--input, --output und --version sind Pflicht.";
            return null;
        }

        // Git-Tags heißen „v0.1.0“, im Manifest steht „0.1.0“.
        if (version.StartsWith('v'))
            version = version[1..];

        if (!ManifestValidator.IsValidVersion(version))
        {
            error = $"„{version}“ ist keine gültige Version (erwartet z. B. 0.1.0).";
            return null;
        }

        executable = (executable ?? DefaultExecutable).Replace('\\', '/');
        return new BuilderOptions(input, output, version, executable, excludes);
    }
}
