using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using AoS2.Contracts;
using AoS2.JoinTickets;
using AoS2.Net;
using AoS2.Shared;
using AoS2.Shared.Net;
using LiteNetLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace AoS2.Server.Tests;

/// <summary>Abfragen ohne Verbindung über echtes UDP: Antwort des Servers, Nachweis, Grenzen, Ping im Client.</summary>
[Collection("UDP")]
public sealed class ServerQueryUdpTests : IDisposable
{
    private readonly GameServer _server;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ServerQueryUdpTests()
    {
        var tickets = new TicketCheck(null, JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, TimeProvider.System), TimeProvider.System);
        _server = new GameServer(TestArena.Content, new MatchSettings { PlayersPerTeam = 3 }, tickets, NullLogger.Instance, "Testserver");
        _server.Start(0);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private string Address => $"127.0.0.1:{_server.Port}";

    private double Now => _clock.Elapsed.TotalSeconds;

    private PingResult PingUntilDone(ServerPinger pinger, string address, double maxSeconds = 6)
    {
        pinger.Ping(address);
        var end = Now + maxSeconds;
        while (pinger.ResultFor(address)!.State == PingState.Pending && Now < end)
        {
            _server.Update();
            pinger.Poll();
            Thread.Sleep(1);
        }
        return pinger.ResultFor(address)!;
    }

    [Fact]
    public void Ping_GetsNameMapAndSlots_AndRoundTripTime()
    {
        using var pinger = new ServerPinger(() => Now);

        var result = PingUntilDone(pinger, Address);

        Assert.Equal(PingState.Answered, result.State);
        Assert.InRange(result.RoundTripMs, 0, 200);
        Assert.True(result.Info.IsCompatible);
        Assert.Equal(("Testserver", TestArena.Content.MapName, 0, 6), (result.Info.Name, result.Info.Map, result.Info.Players, result.Info.MaxPlayers));
        Assert.Null(result.Info.Proof); // ohne Challenge kein Nachweis
    }

    [Fact]
    public void Ping_CountsConnectedPlayers()
    {
        using var client = new NetClient();
        client.Session = null;
        client.Connect(new ServerAddress { Host = "127.0.0.1", Port = _server.Port }, DevJoinTicket.Create(Guid.NewGuid(), "Jan", DateTimeOffset.UtcNow));
        var end = Now + 5;
        while (_server.Match.Players.Count == 0 && Now < end)
        {
            _server.Update();
            client.Poll();
            Thread.Sleep(1);
        }
        using var pinger = new ServerPinger(() => Now);

        var result = PingUntilDone(pinger, Address);

        Assert.Equal(1, result.Info.Players);
        Assert.Equal(1, _server.PlayerCount);
    }

    [Fact]
    public void Ping_ToClosedPort_GivesNoAnswer_AfterAllAttempts()
    {
        using var unused = new UdpClient(0);
        var deadPort = ((IPEndPoint)unused.Client.LocalEndPoint!).Port;
        using var pinger = new ServerPinger(() => Now);
        var started = Now;

        var result = PingUntilDone(pinger, $"127.0.0.1:{deadPort}");

        Assert.Equal(PingState.NoAnswer, result.State);
        Assert.InRange(Now - started, ServerPinger.Attempts * ServerPinger.AttemptSeconds - 0.1, 6);
    }

    [Theory]
    [InlineData("kein gültiger name")]
    [InlineData("gibt-es-nicht.invalid:32887")]
    public void Ping_ToInvalidAddress_IsUnresolvable(string address)
    {
        using var pinger = new ServerPinger(() => Now);

        Assert.Equal(PingState.Unresolvable, PingUntilDone(pinger, address).State);
    }

    [Fact]
    public void Challenge_IsAnsweredWithProofFromTheServerSecret()
    {
        using var probe = new RawQueries();
        var challenge = RandomNumberGenerator.GetBytes(ServerQuery.ChallengeBytes);

        probe.Send(_server.Port, new ServerQuery { Nonce = 99, Challenge = challenge });
        var info = Assert.Single(probe.Receive(_server, 1));

        Assert.Equal(99u, info.Nonce);
        Assert.True(ServerProof.Matches(_server.Secret, challenge, info.Proof));
        Assert.False(ServerProof.Matches(RandomNumberGenerator.GetBytes(32), challenge, info.Proof));
    }

    [Fact]
    public void Queries_ArePerIpLimited()
    {
        using var probe = new RawQueries();
        for (uint i = 0; i < 40; i++) probe.Send(_server.Port, new ServerQuery { Nonce = i });

        var answers = probe.Receive(_server, 40, maxSeconds: 1.5).Count;

        // Höchstens 10 pro Sekunde; fallen die Anfragen auf eine Sekundengrenze, können es zweimal 10 sein.
        Assert.InRange(answers, GameServer.MaxQueriesPerIpPerSecond, 2 * GameServer.MaxQueriesPerIpPerSecond);
    }

    [Fact]
    public void ForeignPackets_AreIgnored()
    {
        using var probe = new RawQueries();
        probe.SendRaw(_server.Port, RandomNumberGenerator.GetBytes(ServerQuery.Size));
        probe.SendRaw(_server.Port, [1, 2, 3]);
        Assert.Empty(probe.Receive(_server, 1, maxSeconds: 0.5));

        probe.Send(_server.Port, new ServerQuery { Nonce = 5 });
        Assert.Single(probe.Receive(_server, 1));
    }

    /// <summary>Schickt Abfragen direkt über LiteNetLib und sammelt die Antworten.</summary>
    private sealed class RawQueries : INetEventListener, IDisposable
    {
        private readonly NetManager _net;
        private readonly List<ServerInfo> _answers = new();

        public RawQueries()
        {
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true };
            _net.Start();
        }

        public void Send(int port, ServerQuery query) => SendRaw(port, query.ToBytes());

        public void SendRaw(int port, byte[] data) => _net.SendUnconnectedMessage(data, new IPEndPoint(IPAddress.Loopback, port));

        public List<ServerInfo> Receive(GameServer server, int expected, double maxSeconds = 3)
        {
            var clock = Stopwatch.StartNew();
            while (_answers.Count < expected && clock.Elapsed.TotalSeconds < maxSeconds)
            {
                server.Update();
                _net.PollEvents();
                Thread.Sleep(1);
            }
            // Kurz nachwarten, ob mehr kommt als erwartet.
            var settle = Stopwatch.StartNew();
            while (settle.Elapsed.TotalSeconds < 0.1)
            {
                server.Update();
                _net.PollEvents();
                Thread.Sleep(1);
            }
            return _answers;
        }

        public void Dispose() => _net.Stop();

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) =>
            _answers.Add(ServerInfo.Read(reader.RawData, reader.Position, reader.AvailableBytes));

        void INetEventListener.OnPeerConnected(NetPeer peer) { }
        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }
        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) { }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        void INetEventListener.OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}

/// <summary>Serverliste im Client ohne Netz: Protokollversion, Filter, Sortierung.</summary>
public class ServerListTests
{
    private static ServerListEntry Entry(string address, int players = 0, int max = 32, string map = "testmap", bool official = false,
        int protocol = ProtocolInfo.Version, string? name = null) => new()
    {
        Address = address, Name = name ?? address, Map = map, Players = players, MaxPlayers = max, ProtocolVersion = protocol, Official = official,
    };

    private static PingResult Answered(int ms, int players = 0, int max = 32, int protocol = ProtocolInfo.Version) => new()
    {
        State = PingState.Answered, RoundTripMs = ms,
        Info = new ServerInfo { ProtocolVersion = protocol, Players = players, MaxPlayers = max, Name = "x", Map = "testmap" },
    };

    private static List<string> Addresses(IEnumerable<ServerRow> rows) => rows.Select(r => r.Entry.Address).ToList();

    [Fact]
    public void ServersWithOtherProtocolVersion_AreHidden()
    {
        var list = new ServerList();
        list.Set([Entry("a:1"), Entry("alt:1", protocol: ProtocolInfo.Version - 1), Entry("neu:1", protocol: ProtocolInfo.Version + 1)]);

        Assert.Equal(["a:1"], Addresses(list.Visible(new ServerFilter(), null)));
    }

    [Fact]
    public void ServerAnsweringWithOtherVersion_IsHiddenToo()
    {
        var list = new ServerList();
        list.Set([Entry("a:1"), Entry("b:1")]);
        var pings = new Dictionary<string, PingResult> { ["b:1"] = Answered(10, protocol: ProtocolInfo.Version + 1) };

        Assert.Equal(["a:1"], Addresses(list.Visible(new ServerFilter(), a => pings.GetValueOrDefault(a))));
    }

    [Fact]
    public void Filters_FullEmptyAndMap()
    {
        var list = new ServerList();
        list.Set([Entry("voll:1", 8, 8), Entry("leer:1", 0), Entry("halb:1", 4, 8), Entry("andere:1", 2, map: "insel")]);

        Assert.Equal(["halb:1", "andere:1", "leer:1"], Addresses(list.Visible(new ServerFilter { HideFull = true }, null)));
        Assert.Equal(["voll:1", "halb:1", "andere:1"], Addresses(list.Visible(new ServerFilter { HideEmpty = true }, null)));
        Assert.Equal(["andere:1"], Addresses(list.Visible(new ServerFilter { Map = "INSEL" }, null)));
        Assert.Equal(["insel", "testmap"], list.Maps);
    }

    [Fact]
    public void PlayersFromThePingAnswer_WinOverTheHeartbeat()
    {
        var list = new ServerList();
        list.Set([Entry("a:1", players: 0, max: 8)]);
        var pings = new Dictionary<string, PingResult> { ["a:1"] = Answered(20, players: 8, max: 8) };

        var row = Assert.Single(list.Visible(new ServerFilter(), a => pings.GetValueOrDefault(a)));

        Assert.True(row.IsFull);
        Assert.Empty(list.Visible(new ServerFilter { HideFull = true }, a => pings.GetValueOrDefault(a)));
    }

    [Fact]
    public void Sorting_OfficialFirst_ThenByChoice()
    {
        var list = new ServerList();
        list.Set([Entry("a:1", 2, name: "Charlie"), Entry("b:1", 9, name: "alpha"), Entry("off:1", 0, official: true, name: "Offiziell"), Entry("c:1", 5, name: "Bravo")]);
        var pings = new Dictionary<string, PingResult> { ["a:1"] = Answered(15, 2), ["b:1"] = Answered(80, 9), ["c:1"] = Answered(40, 5) };
        PingResult? Ping(string a) => pings.GetValueOrDefault(a);

        Assert.Equal(["off:1", "b:1", "c:1", "a:1"], Addresses(list.Visible(new ServerFilter { Sort = ServerSort.Players }, Ping)));
        Assert.Equal(["off:1", "a:1", "c:1", "b:1"], Addresses(list.Visible(new ServerFilter { Sort = ServerSort.Ping }, Ping))); // ohne Ping ans Ende, aber offiziell bleibt oben
        Assert.Equal(["off:1", "b:1", "c:1", "a:1"], Addresses(list.Visible(new ServerFilter { Sort = ServerSort.Name }, Ping)));
    }

    [Fact]
    public void DuplicateAndBrokenEntries_AreDropped()
    {
        var list = new ServerList();
        list.Set([Entry("a:1"), Entry("A:1"), new ServerListEntry { ProtocolVersion = ProtocolInfo.Version }, null!]);

        Assert.Single(list.Entries);
    }
}

/// <summary>Heartbeat gegen ein nachgebildetes Backend: Intervall, Fehlermeldungen, Abmelden.</summary>
public class HeartbeatTests : IDisposable
{
    private readonly FakeBackend _backend = new();
    private readonly GameServer _server;

    public HeartbeatTests()
    {
        var tickets = new TicketCheck(null, JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, TimeProvider.System), TimeProvider.System);
        _server = new GameServer(TestArena.Content, new MatchSettings { PlayersPerTeam = 2 }, tickets, NullLogger.Instance, "Mein Server");
    }

    public void Dispose() => _server.Dispose();

    private Heartbeat Create(string? officialKey = null) =>
        new(new HttpClient(_backend), "https://aos2.example.org/", _server, "play.example.org:32887", officialKey, NullLogger.Instance);

    [Fact]
    public async Task SendsServerInfo_AndUsesTheIntervalFromTheBackend()
    {
        _backend.Respond = _ => JsonContent.Create(new ServerHeartbeatResponse { HeartbeatSeconds = 20, ExpirySeconds = 60 });
        var heartbeat = Create("geheim");

        var next = await heartbeat.SendAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(20), next);
        Assert.True(heartbeat.Listed);
        var (uri, request) = Assert.Single(_backend.Heartbeats);
        Assert.Equal("https://aos2.example.org/api/servers/heartbeat", uri);
        Assert.Equal(("play.example.org:32887", "Mein Server", TestArena.Content.MapName, 0, 4, ProtocolInfo.Version, "geheim"),
            (request.Address, request.Name, request.Map, request.Players, request.MaxPlayers, request.ProtocolVersion, request.OfficialKey));
        Assert.Equal(_server.Secret, Convert.FromBase64String(request.Secret));
    }

    [Fact]
    public async Task Rejection_KeepsTheReason_AndRetriesLater()
    {
        _backend.Status = HttpStatusCode.UnprocessableEntity;
        _backend.Respond = _ => JsonContent.Create(new { title = "Der Server ist unter play.example.org:32887 von außen nicht erreichbar." });
        var heartbeat = Create();

        var next = await heartbeat.SendAsync(CancellationToken.None);

        Assert.False(heartbeat.Listed);
        Assert.Equal("Der Server ist unter play.example.org:32887 von außen nicht erreichbar (HTTP 422)", heartbeat.Problem);
        Assert.Equal(Heartbeat.RetryAfterFailure, next);
    }

    [Fact]
    public async Task TooManyRequests_WaitsAsLongAsTheBackendSays()
    {
        _backend.Status = HttpStatusCode.TooManyRequests;
        _backend.RetryAfter = TimeSpan.FromMinutes(5);
        _backend.Respond = _ => JsonContent.Create(new { title = "Zu viele Anmeldeversuche." });

        var next = await Create().SendAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), next);
    }

    [Fact]
    public async Task UnreachableWebsite_IsReported()
    {
        _backend.Fail = true;
        var heartbeat = Create();

        await heartbeat.SendAsync(CancellationToken.None);

        Assert.False(heartbeat.Listed);
        Assert.Contains("nicht erreichbar", heartbeat.Problem);
    }

    [Fact]
    public async Task Run_EndsQuietly_WhenStoppedDuringAHeartbeat()
    {
        _backend.Delay = TimeSpan.FromSeconds(10);
        using var stop = new CancellationTokenSource();
        var run = Create().RunAsync(stop.Token);

        await Task.Delay(100);
        await stop.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(2)); // ohne Exception
    }

    [Fact]
    public async Task Remove_OnlyAfterBeingListed()
    {
        _backend.Respond = _ => JsonContent.Create(new ServerHeartbeatResponse { HeartbeatSeconds = 30 });
        var heartbeat = Create();
        await heartbeat.RemoveAsync(CancellationToken.None);
        Assert.Empty(_backend.Removes);

        await heartbeat.SendAsync(CancellationToken.None);
        _backend.Status = HttpStatusCode.NoContent;
        await heartbeat.RemoveAsync(CancellationToken.None);

        var remove = Assert.Single(_backend.Removes);
        Assert.Equal("play.example.org:32887", remove.Address);
        Assert.False(heartbeat.Listed);
    }

    private sealed class FakeBackend : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TimeSpan? RetryAfter { get; set; }
        public Func<HttpRequestMessage, HttpContent?> Respond { get; set; } = _ => null;
        public bool Fail { get; set; }
        public TimeSpan Delay { get; set; }
        public List<(string Uri, ServerHeartbeatRequest Request)> Heartbeats { get; } = new();
        public List<ServerRemoveRequest> Removes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("Verbindung abgelehnt");
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith(ApiRoutes.ServerHeartbeat))
                Heartbeats.Add((uri, (await request.Content!.ReadFromJsonAsync<ServerHeartbeatRequest>(cancellationToken))!));
            else if (uri.EndsWith(ApiRoutes.ServerRemove))
                Removes.Add((await request.Content!.ReadFromJsonAsync<ServerRemoveRequest>(cancellationToken))!);
            var response = new HttpResponseMessage(Status) { Content = Respond(request) };
            if (RetryAfter is { } retry) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retry);
            return response;
        }
    }
}

public class ServerBrowserOptionsTests
{
    [Fact]
    public void Name_FromCommandLine_AndValidated()
    {
        Assert.Equal("Jans Server", ServerOptions.Parse(["--dev", "--name", "Jans Server"]).Name);
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--dev", "--name", ""]));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--dev", "--name", new string('x', 41)]));
    }

    [Fact]
    public void Address_IsNormalizedLikeTheClientWritesItIntoTickets()
    {
        var options = ServerOptions.Parse(["--site", "https://aos2.example.org", "--address", "Play.Example.ORG"]);

        Assert.Equal("play.example.org:32887", options.Address);
    }

    [Fact]
    public void InvalidAddress_Throws() =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--site", "https://aos2.example.org", "--address", "play example:1"]));

    [Fact]
    public void Listed_IsOnByDefault_AndCanBeSwitchedOff()
    {
        Assert.True(ServerOptions.Parse(["--dev"]).Listed);
        Assert.False(ServerOptions.Parse(["--dev", "--unlisted"]).Listed);

        var config = Path.Combine(Path.GetTempPath(), $"aos2-listed-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, """{ "dev": true, "listed": false, "name": "Privat" }""");
        try
        {
            var options = ServerOptions.Parse(["--config", config]);
            Assert.Equal((false, "Privat"), (options.Listed, options.Name));
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void ExampleConfig_IsValid_AndListsTheServer()
    {
        var options = ServerOptions.Parse(["--config", TestArena.RepoFile("server/AoS2.Server/server.example.json")]);

        Assert.True(options.Listed);
        Assert.False(options.DevMode);
        Assert.Equal("play.example.org:32887", options.Address);
    }
}
