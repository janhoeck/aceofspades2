using AoS2.Server;
using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Net;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8; // Umlaute auch in der klassischen Windows-Konsole

using var loggers = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));
var log = loggers.CreateLogger("AoS2.Server");

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(ServerOptions.Usage);
    return 0;
}

ServerOptions options;
try
{
    options = ServerOptions.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // sauber herunterfahren statt sofort beenden
    stop.Cancel();
};
// docker stop schickt SIGTERM (kein Strg+C): ebenfalls sauber beenden und aus dem Server-Browser abmelden.
using var sigterm = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

if (options.DevMode)
{
    log.LogWarning("==================================================================================");
    log.LogWarning(" ENTWICKLUNGSMODUS (--dev): Der Server nimmt Entwicklungs-Tickets an, die jeder");
    log.LogWarning(" Client ohne Anmeldung selbst ausstellen kann. Nur zum lokalen Testen verwenden,");
    log.LogWarning(" nie für einen öffentlichen Server.");
    log.LogWarning("==================================================================================");
}

GameContent content;
TicketCheck tickets;
try
{
    content = GameContent.Load(options);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    tickets = await TicketCheck.CreateAsync(options, http, TimeProvider.System, stop.Token);
}
catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
{
    log.LogError("Start fehlgeschlagen: Die Website {Site} ist nicht erreichbar, der Server braucht von dort die Schlüssel für Join-Tickets ({Message}). Stimmt \"site\"? Zum Testen ohne Backend: --dev und kein site.",
        options.SiteUrl, e.Message);
    return 1;
}
catch (Exception e) when (e is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
{
    log.LogError("Start fehlgeschlagen: {Message}", e.Message);
    return 1;
}

using var server = new GameServer(content, options.Match, tickets, log, options.Name);
var world = server.Match.World;
log.LogInformation("\"{Name}\": Ace of Spades 2 Server {Version}, Protokoll {Protocol}, Map {Map} ({X} × {Y} × {Z}){Config}{Tickets}",
    options.Name, Heartbeat.Version, ProtocolInfo.Version, content.MapName, world.SizeX, world.SizeY, world.SizeZ,
    options.ConfigFile != null ? $", Config {options.ConfigFile}" : "",
    options.SiteUrl != null ? $", Join-Tickets von {options.SiteUrl} für {options.Address}" : "");
var rules = options.Match;
log.LogInformation("Team Deathmatch: {Blue} gegen {Green}, bis {Score} Punkte{Time}, Teambeschuss {FriendlyFire}, {End} s Pause nach dem Matchende",
    rules.PlayersPerTeam, rules.PlayersPerTeam, rules.ScoreLimit, rules.TimeLimitMinutes > 0 ? $" oder {rules.TimeLimitMinutes} min" : "",
    rules.FriendlyFire ? "an" : "aus", rules.MatchEndSeconds);
try
{
    server.Start(options.Port);
}
catch (InvalidOperationException e)
{
    log.LogError("{Message}", e.Message);
    return 1;
}
if (options.Address != null && ServerAddress.TryParse(options.Address, out var advertised) && advertised.Port != server.Port)
    log.LogWarning("address nennt Port {Advertised}, der Server lauscht auf {Port}. Das stimmt nur, wenn der Router den einen auf den anderen weiterleitet.",
        advertised.Port, server.Port);

// Server-Browser: Heartbeat ans Backend, neben der Hauptschleife.
using var browserHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
browserHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"AoS2.Server/{Heartbeat.Version}");
Heartbeat? heartbeat = null;
Task heartbeatTask = Task.CompletedTask;
if (options.SiteUrl == null)
    log.LogInformation("Nicht im Server-Browser (ohne site). Spieler verbinden sich direkt über die Adresse.");
else if (options.DevMode)
    log.LogInformation("Nicht im Server-Browser: Server im Entwicklungsmodus werden nie gelistet.");
else if (!options.Listed)
    log.LogInformation("Nicht im Server-Browser (listed: false). Spieler verbinden sich direkt über {Address}.", options.Address);
else
{
    heartbeat = new Heartbeat(browserHttp, options.SiteUrl, server, options.Address!, Environment.GetEnvironmentVariable(Heartbeat.OfficialKeyVariable), log);
    heartbeatTask = Task.Run(() => heartbeat.RunAsync(stop.Token));
}

// Jede Minute eine Zeile zum Zustand: Spieler, Punkte, wie lange ein Tick rechnet und wie viel der Server verschickt.
const double reportSeconds = 60;
ServerLoop.Run(server, stop.Token, () =>
{
    var s = server.Stats;
    var m = server.Match;
    if (m.Players.Count > 0)
        log.LogInformation("Status: {Players} Spieler (Blau {Blue}, Grün {Green}), Punkte {ScoreBlue} : {ScoreGreen}, Tick Ø {Avg:0.00} ms / max {Max:0.0} ms{Slow}, senden {Rate:0} kB/s",
            m.Players.Count, m.TeamSize(Team.Blue), m.TeamSize(Team.Green), m.Info.ScoreBlue, m.Info.ScoreGreen, s.AverageStepMs, s.MaxStepMs,
            s.SlowTicks > 0 ? $" ({s.SlowTicks} Ticks zu langsam)" : "", s.SentBytes / 1024.0 / reportSeconds);
    s.Reset();
}, reportSeconds);
log.LogInformation("Server wird beendet …");
await heartbeatTask;
if (heartbeat != null)
{
    using var removeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await heartbeat.RemoveAsync(removeTimeout.Token);
}
server.Stop();
return 0;
