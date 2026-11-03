using AoS2.Server;
using AoS2.Shared;
using Microsoft.Extensions.Logging;

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
catch (Exception e) when (e is IOException or FormatException or InvalidDataException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
{
    log.LogError("Start fehlgeschlagen: {Message}", e.Message);
    return 1;
}

using var server = new GameServer(content, options.MaxPlayers, tickets, log);
var world = server.Match.World;
log.LogInformation("Ace of Spades 2 Server, Protokoll {Protocol}, Map {Map} ({X} × {Y} × {Z}), höchstens {Max} Spieler{Tickets}",
    ProtocolInfo.Version, content.MapName, world.SizeX, world.SizeY, world.SizeZ, options.MaxPlayers,
    options.SiteUrl != null ? $", Join-Tickets von {options.SiteUrl} für {options.Address}" : "");
try
{
    server.Start(options.Port);
}
catch (InvalidOperationException e)
{
    log.LogError("{Message}", e.Message);
    return 1;
}

ServerLoop.Run(server, stop.Token);
log.LogInformation("Server wird beendet …");
server.Stop();
return 0;
