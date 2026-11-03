using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AoS2.JoinTickets;
using AoS2.Net;
using AoS2.Shared;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using DisconnectReason = AoS2.Shared.Net.DisconnectReason;

namespace AoS2.Server.Tests;

/// <summary>
/// Echter Server und echte Clients über UDP auf localhost (LiteNetLib), alles in einem Prozess.
/// Server und Clients rechnen in Echtzeit mit 60 Ticks/s, wie im Spiel.
/// </summary>
[Collection("UDP")]
public sealed class UdpIntegrationTests : IDisposable
{
    private readonly GameServer _server;
    private readonly TicketCheck _tickets;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<UdpTestClient> _clients = new();
    private double _nextServerTick;

    public UdpIntegrationTests()
    {
        _tickets = new TicketCheck(null, JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, TimeProvider.System), TimeProvider.System);
        _server = new GameServer(TestArena.Content, 8, _tickets, NullLogger.Instance);
        _server.Start(0);
    }

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    private ServerAddress Address => new() { Host = "127.0.0.1", Port = _server.Port };

    private UdpTestClient Connect(string name, string? ticket = null, int packetLoss = 0)
    {
        var client = new UdpTestClient(() => Now) { Next = Now };
        client.Net.SimulatedPacketLoss = packetLoss;
        client.Net.Connect(Address, ticket ?? DevJoinTicket.Create(Guid.NewGuid(), name, DateTimeOffset.UtcNow));
        _clients.Add(client);
        return client;
    }

    /// <summary>Server und Clients in Echtzeit laufen lassen, bis die Bedingung gilt.</summary>
    private bool Pump(Func<bool> until, double maxSeconds = 5)
    {
        var end = Now + maxSeconds;
        while (Now < end)
        {
            _server.Update();
            while (Now >= _nextServerTick)
            {
                _server.Match.Step();
                _nextServerTick += 1.0 / 60;
            }
            foreach (var c in _clients) c.Update();
            if (until()) return true;
            Thread.Sleep(1);
        }
        return until();
    }

    private void PumpFor(double seconds) => Pump(() => false, seconds);

    [Fact]
    public void TwoClients_MoveAndEditBlocks_OverRealUdp()
    {
        _nextServerTick = Now;
        var a = Connect("Anna", packetLoss: 5);
        var b = Connect("Ben", packetLoss: 5);
        Assert.True(Pump(() => a.Playing && b.Playing && a.Session.RemotePlayers.Count == 1 && b.Session.RemotePlayers.Count == 1),
            $"Beitritt: A {a.Session.Phase} {a.Net.DisconnectMessage}, B {b.Session.Phase} {b.Net.DisconnectMessage}");
        Assert.NotEqual(a.Session.PlayerId, b.Session.PlayerId);

        // Anna läuft eine Sekunde vorwärts.
        var start = a.Session.Prediction.State.Position;
        a.Input.Buttons = InputButtons.Forward;
        PumpFor(1);
        a.Input = default;
        PumpFor(0.6);

        var serverA = _server.Match.FindPlayer(a.Session.PlayerId)!;
        Assert.True(serverA.State.Position.Z - start.Z > 3.5f, $"auf dem Server gelaufen: {start} → {serverA.State.Position}");
        Assert.True(ClientPrediction.SameState(serverA.State, a.Session.Prediction.State), "Vorhersage = Server");
        var seenByB = b.Session.RemotePlayers[a.Session.PlayerId].Current.Position;
        Assert.Equal(serverA.State.Position, seenByB);

        // Ben baut einen Block ab, auf den er schaut.
        var benFeet = b.Session.Prediction.State.Position;
        (b.Input.Yaw, b.Input.Pitch) = TestArena.Aim(benFeet, new Vec3(benFeet.X + 0.3f, 4f, benFeet.Z + 2f));
        PumpFor(0.1);
        Assert.True(b.Target(out var hit));
        b.EditAfterTick = c => c.Session.RequestRemove(hit.X, hit.Y, hit.Z, hit.Distance);
        Assert.True(Pump(() => !a.Session.World.IsSolid(hit.X, hit.Y, hit.Z) && b.Session.Blocks.PendingCount == 0 && b.EditAfterTick == null, 2));
        Assert.False(_server.Match.World.IsSolid(hit.X, hit.Y, hit.Z));
        Assert.Empty(b.Rejected);
    }

    [Fact]
    public void LateJoiner_GetsWorldChanges()
    {
        _nextServerTick = Now;
        var a = Connect("Anna");
        Assert.True(Pump(() => a.Playing));
        (a.Input.Yaw, a.Input.Pitch) = TestArena.Aim(TestArena.SpawnA, new Vec3(16.5f, 4f, 19.5f));
        PumpFor(0.1);
        a.EditAfterTick = c => c.Session.RequestPlace(16, 4, 19, TestArena.Build, 3f);
        Assert.True(Pump(() => _server.Match.World.IsSolid(16, 4, 19)));

        var c = Connect("Chris");
        Assert.True(Pump(() => c.Playing));
        Assert.Equal(TestArena.Build, c.Session.World.GetBlock(16, 4, 19));
        Assert.True(_server.Match.ChangeCount >= 1);
    }

    [Fact]
    public void InvalidTicket_IsRejectedWithReason()
    {
        _nextServerTick = Now;
        var a = Connect("Anna", ticket: "kein-gültiges-ticket");
        Assert.True(Pump(() => a.Net.DisconnectMessage != null));
        Assert.Equal(DisconnectReason.InvalidTicket, a.Net.DisconnectCode);
        Assert.Equal(ClientPhase.Disconnected, a.Session.Phase);
        Assert.Empty(_server.Match.Players);
    }

    [Fact]
    public void SameTicketTwice_SecondIsRejected()
    {
        _nextServerTick = Now;
        var ticket = DevJoinTicket.Create(Guid.NewGuid(), "Anna", DateTimeOffset.UtcNow);
        var first = Connect("Anna", ticket);
        Assert.True(Pump(() => first.Playing));
        var second = Connect("Anna", ticket);
        Assert.True(Pump(() => second.Net.DisconnectMessage != null));
        Assert.Equal(DisconnectReason.TicketReused, second.Net.DisconnectCode);
        Assert.True(first.Playing);
    }

    [Fact]
    public void SameAccountAgain_OldConnectionIsClosed()
    {
        _nextServerTick = Now;
        var account = Guid.NewGuid();
        var first = Connect("Anna", DevJoinTicket.Create(account, "Anna", DateTimeOffset.UtcNow));
        Assert.True(Pump(() => first.Playing));
        var second = Connect("Anna", DevJoinTicket.Create(account, "Anna", DateTimeOffset.UtcNow));
        Assert.True(Pump(() => second.Playing && first.Net.DisconnectMessage != null));
        Assert.Equal(DisconnectReason.LoggedInElsewhere, first.Net.DisconnectCode);
        Assert.Single(_server.Match.Players);
    }

    [Fact]
    public void WrongProtocolVersion_IsRejectedWithReason()
    {
        _nextServerTick = Now;
        var listener = new EventBasedNetListener();
        DisconnectNotice? notice = null;
        listener.PeerDisconnectedEvent += (_, info) =>
            notice = DisconnectNotice.Parse(info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetRemainingBytes() : null);
        var net = new NetManager(listener);
        try
        {
            net.Start();
            var w = new NetWriter();
            new ConnectRequest { ProtocolVersion = ProtocolInfo.Version + 1, Ticket = "egal" }.Write(w);
            net.Connect("127.0.0.1", _server.Port, NetDataWriter.FromBytes(w.ToArray(), false));
            Assert.True(Pump(() =>
            {
                net.PollEvents();
                return notice != null;
            }));
            Assert.Equal(DisconnectReason.ProtocolMismatch, notice!.Value.Reason);
            Assert.Contains("Protokoll", notice.Value.Message);
        }
        finally
        {
            net.Stop();
        }
    }

    [Fact]
    public void NoServer_ReportsUnreachable()
    {
        // Ein freier Port, auf dem niemand lauscht
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();

        var client = new UdpTestClient(() => Now);
        _clients.Add(client);
        client.Net.Connect(new ServerAddress { Host = "127.0.0.1", Port = port }, "x");
        Assert.True(Pump(() => client.Net.DisconnectMessage != null, 15));
        Assert.Equal(ClientPhase.Disconnected, client.Session.Phase);
    }

    /// <summary>Ein Client wie im Spiel: NetClient + ClientSession, 60 Ticks/s nach der Uhr.</summary>
    private sealed class UdpTestClient : IDisposable
    {
        public UdpTestClient(Func<double> clock)
        {
            Clock = clock;
            Net = new NetClient();
            var content = TestArena.Content;
            Session = new ClientSession(content.Registry, content.Movement,
                map => map == content.MapName ? content.MapFileBytes : null, Net, clock);
            Session.EditRejected += r => Rejected.Add(r);
            Net.Session = Session;
        }

        public Func<double> Clock { get; }
        public NetClient Net { get; }
        public ClientSession Session { get; }
        public PlayerInput Input;
        public double Next;
        public Func<UdpTestClient, BlockEditResult>? EditAfterTick;
        public List<BlockEditResult> Rejected { get; } = new();

        public bool Playing => Session.Phase == ClientPhase.Playing;

        public void Update()
        {
            Net.Poll();
            Session.Update();
            if (Next == 0) Next = Clock();
            while (Clock() >= Next)
            {
                Next += 1.0 / 60;
                if (!Playing) continue;
                Session.Tick(Input);
                if (EditAfterTick != null)
                {
                    EditAfterTick(this);
                    EditAfterTick = null;
                }
            }
        }

        public bool Target(out VoxelHit hit)
        {
            var state = Session.Prediction.State;
            var eye = state.Position + new Vec3(0, Session.Movement.EyeHeight(state.Crouching), 0);
            return VoxelRaycast.Cast(Session.World, eye, PlayerMovement.LookDirection(Input.Yaw, Input.Pitch), BlockEditing.Reach, out hit);
        }

        public void Dispose()
        {
            Net.Disconnect();
            Net.Dispose();
        }
    }
}
