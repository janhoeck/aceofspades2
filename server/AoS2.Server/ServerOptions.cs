using AoS2.Shared;

namespace AoS2.Server;

/// <summary>
/// Einstellungen von der Kommandozeile. Beispiele:
/// <code>
/// AoS2.Server --site https://aos2.example.org --address play.example.org:32887
/// AoS2.Server --dev                      (lokal testen ohne Backend, siehe <see cref="DevMode"/>)
/// </code>
/// </summary>
public sealed class ServerOptions
{
    public const int MaxPlayersLimit = 48;

    public int Port { get; init; } = ProtocolInfo.DefaultPort;

    /// <summary>Name der Map in <see cref="MapsFolder"/> (ohne <c>.aos2map</c>).</summary>
    public string Map { get; init; } = "testmap";

    public int MaxPlayers { get; init; } = 32;

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

    public static string Usage => """
        Ace of Spades 2 – Spielserver

        Optionen:
          --port <zahl>          UDP-Port (Standard 32887)
          --map <name>           Map aus dem maps-Ordner, ohne .aos2map (Standard testmap)
          --max-players <zahl>   höchstens so viele Spieler (Standard 32, höchstens 48)
          --data <ordner>        Spieldaten (Standard: data neben der exe)
          --maps <ordner>        Maps (Standard: maps neben der exe)
          --site <url>           Adresse der Website; der Server prüft Join-Tickets mit ihren öffentlichen Schlüsseln
          --address <host:port>  Adresse dieses Servers, wie Spieler sie eingeben (gehört ins Ticket)
          --dev                  ENTWICKLUNGSMODUS: nimmt auch Entwicklungs-Tickets an, die jeder fälschen kann.
                                 Nur zum lokalen Testen, nie für öffentliche Server.
          --help                 diese Hilfe
        """;

    /// <summary>Liest die Kommandozeile. Wirft <see cref="ArgumentException"/> mit verständlicher Meldung.</summary>
    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        int port = ProtocolInfo.DefaultPort, maxPlayers = 32;
        string map = "testmap";
        string? data = null, maps = null, site = null, address = null;
        var dev = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Nach {arg} fehlt ein Wert.");

            switch (arg)
            {
                case "--port": port = ParseInt(arg, Value(), 1, 65535); break;
                case "--map": map = Value(); break;
                case "--max-players": maxPlayers = ParseInt(arg, Value(), 1, MaxPlayersLimit); break;
                case "--data": data = Value(); break;
                case "--maps": maps = Value(); break;
                case "--site": site = Value().TrimEnd('/'); break;
                case "--address": address = Value(); break;
                case "--dev": dev = true; break;
                default: throw new ArgumentException($"Unbekannte Option {arg}. Hilfe: --help");
            }
        }

        if (site != null && !(Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new ArgumentException("--site muss eine http(s)-Adresse sein.");
        if (site != null && string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Mit --site braucht der Server auch --address (so steht er in den Join-Tickets).");
        if (site == null && !dev)
            throw new ArgumentException("Ohne --site kann der Server keine Join-Tickets prüfen. Zum lokalen Testen: --dev");
        if (map.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || map.Contains(".."))
            throw new ArgumentException("--map ist ein Name ohne Pfad und ohne Endung.");

        var defaults = new ServerOptions();
        return new ServerOptions
        {
            Port = port,
            Map = map,
            MaxPlayers = maxPlayers,
            DataFolder = data ?? defaults.DataFolder,
            MapsFolder = maps ?? defaults.MapsFolder,
            SiteUrl = site,
            Address = address,
            DevMode = dev,
        };
    }

    private static int ParseInt(string name, string value, int min, int max) =>
        int.TryParse(value, out var v) && v >= min && v <= max
            ? v
            : throw new ArgumentException($"{name} muss eine Zahl von {min} bis {max} sein.");
}
