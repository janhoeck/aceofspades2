using System.Diagnostics;
using System.Globalization;
using System.Text;
using AoS2.JoinTickets;
using AoS2.Server;
using AoS2.Shared.Maps;
using AoS2.Shared.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AoS2.LoadTest;

public sealed class LoadTestOptions
{
    public int Bots { get; init; } = 32;

    /// <summary>So lange wird gemessen (nach dem Beitritt aller Bots und einer kurzen Anlaufzeit).</summary>
    public double Seconds { get; init; } = 30;

    public double WarmupSeconds { get; init; } = 3;

    /// <summary>Server, mit dem sich die Bots verbinden (er muss mit <c>--dev</c> laufen). <c>null</c> = Server im selben Prozess.</summary>
    public ServerAddress? Server { get; init; }

    public string DataFolder { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");
    public string MapsFolder { get; init; } = Path.Combine(AppContext.BaseDirectory, "maps");
    public string Map { get; init; } = "testmap";

    /// <summary>Nur Server im selben Prozess: Punkteziel und Pause nach dem Matchende (kleine Werte = Neustart unter Last).</summary>
    public int ScoreLimit { get; init; } = 50;

    public int MatchEndSeconds { get; init; } = 15;
}

/// <summary>Messwerte eines Lasttests.</summary>
public sealed class LoadTestReport
{
    public int Bots;
    public int Joined;
    public int Blue, Green;
    public int Disconnected;
    public double Seconds;

    // Server (nur im selben Prozess)
    public long Ticks;
    public double TicksPerSecond;
    public double AvgStepMs, P99StepMs, MaxStepMs;
    public long SlowTicks;
    public double AvgSnapshotBytes;
    public int MaxSnapshotBytes;
    public double SnapshotsPerClientPerSecond;
    public double PayloadKBPerClientPerSecond;
    public double WireKBPerClientPerSecond;
    public double WireMbitTotal;
    public double PacketsPerSecond;
    public double UploadKBPerClientPerSecond;
    public int ScoreBlue, ScoreGreen, MatchNumber;
    public int BlockChanges;

    // Clients
    public int Kills;
    public int Shots;
    public double ClientSnapshotsPerSecond;
    public double MispredictionsPerBotPerSecond;

    public bool InProcess;

    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        var s = new StringBuilder();
        s.AppendLine(c, $"Bots: {Joined}/{Bots} im Spiel (Blau {Blue}, Grün {Green}), getrennt: {Disconnected}, gemessen {Seconds:0.0} s");
        if (InProcess)
        {
            s.AppendLine(c, $"Server-Tick: {TicksPerSecond:0.0} Ticks/s, Rechenzeit pro Tick (ganzer Lauf, mit Beitritt) Ø {AvgStepMs:0.00} ms, 99 % unter {P99StepMs:0.00} ms, max {MaxStepMs:0.00} ms (Budget 16,67 ms), zu langsam: {SlowTicks}");
            s.AppendLine(c, $"Snapshots: Ø {AvgSnapshotBytes:0} Byte, max {MaxSnapshotBytes} Byte (Grenze 1000), {SnapshotsPerClientPerSecond:0.0}/s pro Client");
            s.AppendLine(c, $"Download pro Client: {PayloadKBPerClientPerSecond:0.0} kB/s Nutzdaten, {WireKBPerClientPerSecond:0.0} kB/s auf der Leitung (mit UDP/IP-Kopf ≈ {WireKBPerClientPerSecond * 8:0} kbit/s)");
            s.AppendLine(c, $"Upload Server gesamt: {WireMbitTotal:0.00} Mbit/s, {PacketsPerSecond:0} Pakete/s; Upload pro Client {UploadKBPerClientPerSecond:0.0} kB/s");
            s.AppendLine(c, $"Spiel: Punkte Blau {ScoreBlue} : {ScoreGreen} Grün (Match {MatchNumber}), {BlockChanges} Blockänderungen");
        }
        s.AppendLine(c, $"Clients: {Shots} Schüsse, {Kills} Kills, {ClientSnapshotsPerSecond:0.0} Snapshots/s je Bot, Fehlvorhersagen {MispredictionsPerBotPerSecond:0.00}/s je Bot");
        return s.ToString();
    }
}

/// <summary>
/// Startet Bots (und im selben Prozess einen Server mit eigener Hauptschleife in einem eigenen Thread), lässt sie spielen und
/// misst: Tick-Dauer und -Rate, Snapshot-Größe, Bandbreite, Ablauf des Matches.
/// </summary>
public static class LoadTestRun
{
    /// <summary>UDP/IPv4-Kopf, den LiteNetLib nicht mitzählt.</summary>
    private const int UdpIpHeaderBytes = 28;

    public static LoadTestReport Run(LoadTestOptions options, Action<string>? progress = null)
    {
        var content = GameContent.Load(new ServerOptions { DataFolder = options.DataFolder, MapsFolder = options.MapsFolder, Map = options.Map, DevMode = true });
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;

        GameServer? server = null;
        Thread? serverThread = null;
        using var stop = new CancellationTokenSource();
        var address = options.Server ?? default;
        if (options.Server == null)
        {
            var tickets = new TicketCheck(null, JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, TimeProvider.System), TimeProvider.System);
            var perTeam = Math.Max(1, Math.Min(ServerOptions.MaxPlayersPerTeam, (options.Bots + 1) / 2));
            server = new GameServer(content, new MatchSettings { PlayersPerTeam = perTeam, ScoreLimit = options.ScoreLimit, MatchEndSeconds = options.MatchEndSeconds }, tickets, NullLogger.Instance)
            {
                Stats = new ServerStats(keepSamples: true),
            };
            server.Start(0);
            address = new ServerAddress { Host = "127.0.0.1", Port = server.Port };
            serverThread = new Thread(() => ServerLoop.Run(server, stop.Token)) { IsBackground = true, Name = "Server" };
            serverThread.Start();
        }

        var bots = new List<Bot>();
        try
        {
            for (var i = 0; i < options.Bots; i++)
            {
                var bot = new Bot($"Bot{i + 1:00}", content, Now, seed: 1000 + i);
                bots.Add(bot);
                bot.Connect(address);
            }

            // Beitreten (alle Bots in einem Thread, 60 Ticks/s jeder).
            var joinUntil = Now() + 20;
            while (Now() < joinUntil && bots.Any(b => !b.InTeam && !b.Disconnected)) Pump(bots);
            progress?.Invoke($"{bots.Count(b => b.InTeam)} von {bots.Count} Bots im Spiel");

            var warmupEnd = Now() + options.WarmupSeconds;
            while (Now() < warmupEnd) Pump(bots);

            // Ab hier messen.
            var serverStats = server?.Stats;
            var wireSentStart = server?.WireSent ?? default;
            var wireReceivedStart = server?.WireReceived ?? default;
            var startStats = Snapshot(serverStats);
            foreach (var b in bots) b.SnapshotsAtStart = b.Session.SnapshotsReceived;
            var mispredictionsStart = bots.Sum(b => b.Session.Prediction?.Mispredictions ?? 0);
            var shotsStart = bots.Sum(b => b.ShotsFired);
            var killsStart = bots.Sum(b => b.Kills);
            var start = Now();
            var lastProgress = start;
            while (Now() - start < options.Seconds)
            {
                Pump(bots);
                if (progress != null && Now() - lastProgress >= 5)
                {
                    lastProgress = Now();
                    progress($"… {Now() - start:0} s, Punkte {bots[0].Session.Match.ScoreBlue} : {bots[0].Session.Match.ScoreGreen}");
                }
            }
            var seconds = Now() - start;
            var endStats = Snapshot(serverStats);

            var joined = bots.Count(b => b.InTeam);
            var report = new LoadTestReport
            {
                Bots = bots.Count,
                Joined = joined,
                Blue = bots.Count(b => b.Session.Team == Team.Blue),
                Green = bots.Count(b => b.Session.Team == Team.Green),
                Disconnected = bots.Count(b => b.Disconnected),
                Seconds = seconds,
                Kills = bots.Sum(b => b.Kills) - killsStart,
                Shots = bots.Sum(b => b.ShotsFired) - shotsStart,
                ClientSnapshotsPerSecond = bots.Average(b => b.Session.SnapshotsReceived - b.SnapshotsAtStart) / seconds,
                MispredictionsPerBotPerSecond = (bots.Sum(b => b.Session.Prediction?.Mispredictions ?? 0) - mispredictionsStart) / (double)Math.Max(1, joined) / seconds,
                InProcess = server != null,
            };
            if (server != null && serverStats != null)
            {
                var ticks = endStats.Ticks - startStats.Ticks;
                var snapshots = endStats.Snapshots - startStats.Snapshots;
                var sent = server.WireSent;
                var received = server.WireReceived;
                var wireBytes = sent.Bytes - wireSentStart.Bytes + (sent.Packets - wireSentStart.Packets) * UdpIpHeaderBytes;
                var clients = Math.Max(1, joined);
                report.Ticks = ticks;
                report.TicksPerSecond = ticks / seconds;
                report.AvgStepMs = serverStats.AverageStepMs; // über den ganzen Lauf; Spitzen und Perzentil ebenso
                report.P99StepMs = serverStats.StepPercentile(0.99);
                report.MaxStepMs = serverStats.MaxStepMs;
                report.SlowTicks = serverStats.SlowTicks;
                report.AvgSnapshotBytes = snapshots == 0 ? 0 : (double)(endStats.SnapshotBytes - startStats.SnapshotBytes) / snapshots;
                report.MaxSnapshotBytes = serverStats.MaxSnapshotBytes;
                report.SnapshotsPerClientPerSecond = snapshots / (double)clients / seconds;
                report.PayloadKBPerClientPerSecond = (endStats.SentBytes - startStats.SentBytes) / 1024.0 / clients / seconds;
                report.WireKBPerClientPerSecond = wireBytes / 1024.0 / clients / seconds;
                report.WireMbitTotal = wireBytes * 8 / 1_000_000.0 / seconds;
                report.PacketsPerSecond = (sent.Packets - wireSentStart.Packets) / seconds;
                report.UploadKBPerClientPerSecond = (received.Bytes - wireReceivedStart.Bytes + (received.Packets - wireReceivedStart.Packets) * UdpIpHeaderBytes) / 1024.0 / clients / seconds;
                report.ScoreBlue = server.Match.Info.ScoreBlue;
                report.ScoreGreen = server.Match.Info.ScoreGreen;
                report.MatchNumber = server.Match.Info.MatchNumber;
                report.BlockChanges = server.Match.ChangeCount;
            }
            return report;
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
            stop.Cancel();
            serverThread?.Join();
            server?.Stop();
            server?.Dispose();
        }
    }

    private static void Pump(List<Bot> bots)
    {
        foreach (var b in bots) b.Update();
        Thread.Sleep(1);
    }

    private readonly record struct StatsSnapshot(long Ticks, long Snapshots, long SnapshotBytes, long SentBytes);

    private static StatsSnapshot Snapshot(ServerStats? s) => s == null ? default : new(s.Ticks, s.Snapshots, s.SnapshotBytes, s.SentBytes);
}
