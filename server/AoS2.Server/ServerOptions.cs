using System.Text.Json;
using AoS2.Shared;

namespace AoS2.Server;

/// <summary>
/// Einstellungen des Servers: aus einer Config-Datei (<c>--config server.json</c>, siehe <c>server.example.json</c>) und
/// von der Kommandozeile. Was auf der Kommandozeile steht, gilt vor der Datei. Beispiele:
/// <code>
/// AoS2.Server --config server.json
/// AoS2.Server --site https://aos2.example.org --address play.example.org:32887
/// AoS2.Server --dev                      (lokal testen ohne Backend, siehe <see cref="DevMode"/>)
/// </code>
/// </summary>
public sealed class ServerOptions
{
    public const int MaxPlayersLimit = 48;
    public const int MaxPlayersPerTeam = MaxPlayersLimit / 2;

    /// <summary>Name des Servers (Anzeige; ab M7 im Server-Browser).</summary>
    public string Name { get; init; } = "Ace of Spades 2";

    public int Port { get; init; } = ProtocolInfo.DefaultPort;

    /// <summary>Name der Map in <see cref="MapsFolder"/> (ohne <c>.aos2map</c>).</summary>
    public string Map { get; init; } = "testmap";

    /// <summary>Regeln des Matches: Spieler pro Team, Punkteziel, Zeitlimit, Pause nach dem Matchende, Teambeschuss.</summary>
    public MatchSettings Match { get; init; } = new();

    /// <summary>Höchstens so viele Spieler (zwei volle Teams).</summary>
    public int MaxPlayers => Match.MaxPlayers;

    /// <summary>Ordner mit <c>blocks.json</c>, <c>movement.json</c>, <c>player_rig.json</c>.</summary>
    public string DataFolder { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");

    public string MapsFolder { get; init; } = Path.Combine(AppContext.BaseDirectory, "maps");

    /// <summary>Adresse der Website (Aussteller der Join-Tickets, öffentliche Schlüssel unter <c>/api/.well-known/jwks.json</c>).</summary>
    public string? SiteUrl { get; init; }

    /// <summary>Adresse dieses Servers, wie Spieler sie eingeben (z. B. <c>play.example.org:32887</c>). Steht im Ticket.</summary>
    public string? Address { get; init; }

    /// <summary>
    /// <b>Entwicklungsmodus</b> (<c>--dev</c>): Der Server nimmt zusätzlich Entwicklungs-Tickets an, die jeder Client ohne
    /// Backend selbst ausstellen kann. Nur zum lokalen Testen. Ist nie von selbst an, nur mit dem Schalter.
    /// </summary>
    public bool DevMode { get; init; }

    /// <summary>Gelesene Config-Datei (oder <c>null</c>).</summary>
    public string? ConfigFile { get; init; }

    public static string Usage => """
        Ace of Spades 2 – Spielserver

        Optionen (gelten vor den Werten aus der Config-Datei):
          --config <datei>            Config-Datei (JSON, Vorlage: server.example.json neben der exe)
          --port <zahl>               UDP-Port (Standard 32887)
          --map <name>                Map aus dem maps-Ordner, ohne .aos2map (Standard testmap)
          --players-per-team <zahl>   Spieler pro Team (Standard 16, höchstens 24)
          --score-limit <zahl>        Punkte zum Sieg (Standard 50)
          --data <ordner>             Spieldaten (Standard: data neben der exe)
          --maps <ordner>             Maps (Standard: maps neben der exe)
          --site <url>                Adresse der Website; der Server prüft Join-Tickets mit ihren öffentlichen Schlüsseln
          --address <host:port>       Adresse dieses Servers, wie Spieler sie eingeben (gehört ins Ticket)
          --dev                       ENTWICKLUNGSMODUS: nimmt auch Entwicklungs-Tickets an, die jeder fälschen kann.
                                      Nur zum lokalen Testen, nie für öffentliche Server.
          --help                      diese Hilfe

        Nur in der Config-Datei: name, timeLimitMinutes, matchEndSeconds, friendlyFire.
        """;

    /// <summary>Liest Config-Datei und Kommandozeile. Wirft <see cref="ArgumentException"/> mit verständlicher Meldung.</summary>
    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        var v = new Values();

        // Zuerst die Config-Datei, damit die Kommandozeile ihre Werte überschreiben kann.
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == "--config") v.Config = args[i + 1];
        if (args.Count > 0 && args[^1] == "--config") throw new ArgumentException("Nach --config fehlt ein Wert.");
        if (v.Config != null) ReadConfig(v.Config, v);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Nach {arg} fehlt ein Wert.");

            switch (arg)
            {
                case "--config": Value(); break; // schon gelesen
                case "--port": v.Port = ParseInt(arg, Value(), 1, 65535); break;
                case "--map": v.Map = Value(); break;
                case "--players-per-team": v.PlayersPerTeam = ParseInt(arg, Value(), 1, MaxPlayersPerTeam); break;
                case "--score-limit": v.ScoreLimit = ParseInt(arg, Value(), 1, ushort.MaxValue); break;
                case "--data": v.Data = Value(); break;
                case "--maps": v.Maps = Value(); break;
                case "--site": v.Site = Value(); break;
                case "--address": v.Address = Value(); break;
                case "--dev": v.Dev = true; break;
                default: throw new ArgumentException($"Unbekannte Option {arg}. Hilfe: --help");
            }
        }

        var site = v.Site?.TrimEnd('/');
        if (site != null && !(Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new ArgumentException("site muss eine http(s)-Adresse sein.");
        if (site != null && string.IsNullOrWhiteSpace(v.Address))
            throw new ArgumentException("Mit site braucht der Server auch address (so steht er in den Join-Tickets).");
        if (site == null && !v.Dev)
            throw new ArgumentException("Ohne site (--site oder in der Config) kann der Server keine Join-Tickets prüfen. Zum lokalen Testen: --dev");
        if (v.Map.Length == 0 || v.Map.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || v.Map.Contains(".."))
            throw new ArgumentException("map ist ein Name ohne Pfad und ohne Endung.");

        var defaults = new ServerOptions();
        return new ServerOptions
        {
            Name = v.Name,
            Port = v.Port,
            Map = v.Map,
            Match = new MatchSettings
            {
                PlayersPerTeam = v.PlayersPerTeam,
                ScoreLimit = v.ScoreLimit,
                TimeLimitMinutes = v.TimeLimitMinutes,
                MatchEndSeconds = v.MatchEndSeconds,
                FriendlyFire = v.FriendlyFire,
            },
            DataFolder = v.Data ?? defaults.DataFolder,
            MapsFolder = v.Maps ?? defaults.MapsFolder,
            SiteUrl = site,
            Address = v.Address,
            DevMode = v.Dev,
            ConfigFile = v.Config,
        };
    }

    /// <summary>
    /// Config-Datei lesen: ein JSON-Objekt, Kommentare (<c>//</c>) erlaubt. Unbekannte Einträge sind ein Fehler, damit Tippfehler
    /// auffallen. Relative Ordner (<c>data</c>, <c>maps</c>) gelten relativ zur Config-Datei.
    /// </summary>
    private static void ReadConfig(string path, Values v)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Config-Datei {path} lässt sich nicht lesen: {e.Message}");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Config-Datei {path} ist kein gültiges JSON: {e.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Config-Datei {path}: Erwartet wird ein JSON-Objekt {{ … }}.");
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var e = p.Value;
                switch (p.Name)
                {
                    case "name": v.Name = Text(p.Name, e); break;
                    case "port": v.Port = Int(p.Name, e, 1, 65535); break;
                    case "map": v.Map = Text(p.Name, e); break;
                    case "playersPerTeam": v.PlayersPerTeam = Int(p.Name, e, 1, MaxPlayersPerTeam); break;
                    case "scoreLimit": v.ScoreLimit = Int(p.Name, e, 1, ushort.MaxValue); break;
                    case "timeLimitMinutes": v.TimeLimitMinutes = Int(p.Name, e, 0, 24 * 60); break;
                    case "matchEndSeconds": v.MatchEndSeconds = Int(p.Name, e, 1, 600); break;
                    case "friendlyFire": v.FriendlyFire = Bool(p.Name, e); break;
                    case "data": v.Data = Path.Combine(folder, Text(p.Name, e)); break;
                    case "maps": v.Maps = Path.Combine(folder, Text(p.Name, e)); break;
                    case "site": v.Site = NullableText(p.Name, e); break;
                    case "address": v.Address = NullableText(p.Name, e); break;
                    case "dev": v.Dev = Bool(p.Name, e); break;
                    default: throw new ArgumentException($"Config-Datei {path}: unbekannter Eintrag \"{p.Name}\".");
                }
            }
        }
    }

    private static string Text(string name, JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new ArgumentException($"Config: \"{name}\" muss Text sein.");

    private static string? NullableText(string name, JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : Text(name, e) is { Length: > 0 } s ? s : null;

    private static bool Bool(string name, JsonElement e) =>
        e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new ArgumentException($"Config: \"{name}\" muss true oder false sein.");

    private static int Int(string name, JsonElement e, int min, int max) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && v >= min && v <= max
            ? v
            : throw new ArgumentException($"Config: \"{name}\" muss eine ganze Zahl von {min} bis {max} sein.");

    private static int ParseInt(string name, string value, int min, int max) =>
        int.TryParse(value, out var v) && v >= min && v <= max
            ? v
            : throw new ArgumentException($"{name} muss eine Zahl von {min} bis {max} sein.");

    /// <summary>Werte beim Einlesen, mit den Standardwerten.</summary>
    private sealed class Values
    {
        private static readonly MatchSettings DefaultMatch = new();

        public string? Config;
        public string Name = "Ace of Spades 2";
        public int Port = ProtocolInfo.DefaultPort;
        public string Map = "testmap";
        public int PlayersPerTeam = DefaultMatch.PlayersPerTeam;
        public int ScoreLimit = DefaultMatch.ScoreLimit;
        public int TimeLimitMinutes = DefaultMatch.TimeLimitMinutes;
        public int MatchEndSeconds = DefaultMatch.MatchEndSeconds;
        public bool FriendlyFire = DefaultMatch.FriendlyFire;
        public string? Data, Maps, Site, Address;
        public bool Dev;
    }
}
