namespace AoS2.Updates;

/// <summary>
/// Aufbau des Update-Speichers (R2-Bucket oder lokaler Ordner):
/// <code>
/// game/manifest.json            aktuelle Spielversion (wird nie gecacht)
/// game/manifests/0.1.0.json     Archiv aller Manifeste
/// game/objects/ab/ab12…         Dateiinhalte, benannt nach ihrer SHA-256-Prüfsumme
/// launcher/                     Velopack-Pakete des Launchers (Setup.exe, releases.win.json, …)
/// </code>
/// Weil Dateien nach ihrem Inhalt benannt sind, ändert sich ein Objekt nie. Ein neues Release
/// lädt nur neue Inhalte hoch, und die alte Version bleibt vollständig, bis das neue Manifest da ist.
/// </summary>
public static class UpdateLayout
{
    public const string GameFolder = "game";
    public const string LauncherFolder = "launcher";
    public const string ManifestPath = GameFolder + "/manifest.json";

    public static string VersionedManifestPath(string gameVersion) =>
        $"{GameFolder}/manifests/{gameVersion}.json";

    public static string ObjectPath(string sha256) =>
        $"{GameFolder}/objects/{sha256[..2]}/{sha256}";
}
