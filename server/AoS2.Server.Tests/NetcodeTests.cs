using AoS2.Shared;
using AoS2.Shared.Movement;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;

namespace AoS2.Server.Tests;

/// <summary>
/// Server und Clients über ein simuliertes Netz: Beitritt, Vorhersage und Abgleich mit Verzögerung und Paketverlust,
/// Interpolation anderer Spieler, Blockänderungen über den Server, fallende Blöcke, später Beitritt.
/// </summary>
public class NetcodeTests
{
    private static readonly PlayerInput Idle = default;

    private static bool SameState(PlayerState a, PlayerState b) => ClientPrediction.SameState(a, b);

    private static void AssertWorldsEqual(VoxelWorld expected, VoxelWorld actual)
    {
        for (var y = 0; y < expected.SizeY; y++)
            for (var z = 0; z < expected.SizeZ; z++)
                for (var x = 0; x < expected.SizeX; x++)
                    if (expected.GetBlock(x, y, z) != actual.GetBlock(x, y, z))
                        Assert.Fail($"Block ({x}, {y}, {z}): Server {expected.GetBlock(x, y, z)}, Client {actual.GetBlock(x, y, z)}");
    }

    [Fact]
    public void Join_ReceivesWelcomeMapAndSpawn()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");

        Assert.True(net.RunUntil(() => a.Ready, 1));
        Assert.Equal(TestArena.MapName, a.Session.MapName);
        Assert.Equal("Anna", a.Session.PlayerName);
        Assert.Equal(TestArena.SpawnA, a.State.Position);
        Assert.Equal(64, a.Session.World.SizeX);
        Assert.Empty(a.Session.RemotePlayers);
    }

    [Fact]
    public void Join_WrongMapVersion_FailsWithMessage()
    {
        var content = TestArena.Content;
        var bytes = (byte[])content.MapFileBytes.Clone();
        bytes[^1] ^= 0xFF; // andere Datei, gleicher Name
        var session = new ClientSession(content.Registry, content.Rules, content.RigJson, _ => bytes, new NullSender(), () => 0);

        var welcome = new AoS2.Shared.Net.NetWriter();
        new AoS2.Shared.Net.Welcome { TickRate = 60, SnapshotInterval = 2, MapName = TestArena.MapName, MapChecksum = content.MapChecksum }.Write(welcome);
        session.HandleMessage(welcome.Buffer, 0, welcome.Length);

        Assert.Equal(ClientPhase.Disconnected, session.Phase);
        Assert.Contains("andere Version", session.Error);
    }

    private sealed class NullSender : IMessageSender
    {
        public void Send(AoS2.Shared.Net.NetWriter message, AoS2.Shared.Net.Delivery delivery)
        {
        }
    }

    [Fact]
    public void Prediction_WithLatencyJitterAndLoss_MatchesServerExactly()
    {
        var net = new SimulatedNetwork(seed: 42) { OneWayLatency = 0.06, Jitter = 0.03, Loss = 0.2 };
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));

        // Drei Sekunden Zickzack mit Springen, Sprinten und Ducken
        var frame = 0;
        net.Run(3, () =>
        {
            frame++;
            a.Input.Yaw = frame * 0.5f % 360;
            a.Input.Buttons = InputButtons.Forward
                | (frame % 40 < 20 ? InputButtons.Sprint : InputButtons.Left)
                | (frame % 50 == 0 ? InputButtons.Jump : 0)
                | (frame % 90 > 70 ? InputButtons.Crouch : 0);
        });
        a.Input = Idle;
        net.Run(1);

        var server = net.ServerPlayer(a);
        Assert.InRange(a.Session.Prediction.Tick - server.LastProcessedTick, 0u, 12u); // der Server hängt nur um die Laufzeit hinterher
        Assert.True(SameState(server.State, a.State), $"Server {server.State.Position}, Client {a.State.Position}");
        Assert.Equal(0, a.Session.Prediction.Mispredictions); // Vorhersage war immer richtig, trotz 20 % Verlust
        Assert.Equal(0, server.DroppedInputs);
        Assert.True(Vec3Distance(server.State.Position, TestArena.SpawnA) > 3, "hat sich bewegt");
    }

    [Fact]
    public void Prediction_ClientMovesImmediately_ServerLater()
    {
        var net = new SimulatedNetwork { OneWayLatency = 0.1 };
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));
        a.Input.Buttons = InputButtons.Forward;
        net.Run(0.1);

        // Nach 100 ms hat der Client sich schon bewegt, beim Server ist noch keine Eingabe angekommen.
        Assert.True(a.State.Position.Z > TestArena.SpawnA.Z + 0.2f);
        Assert.Equal(TestArena.SpawnA.Z, net.ServerPlayer(a).State.Position.Z);
    }

    [Fact]
    public void Reconciliation_OtherPlayerDigsHole_ClientIsCorrected()
    {
        var net = new SimulatedNetwork { OneWayLatency = 0.05 };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));

        // Ben schaut auf den Boden vor Anna: Block (16, 3, 19)
        b.LookAt(new Vec3(16.5f, 4f, 19.5f));
        Assert.True(b.Target(out var hit));
        Assert.Equal((16, 3, 19), (hit.X, hit.Y, hit.Z));

        // Anna läuft los; erst wenn sie (vorhergesagt) schon über der Stelle steht, gräbt Ben das Loch.
        a.Input.Buttons = InputButtons.Forward;
        Assert.True(net.RunUntil(() => a.State.Position.Z >= 19.5f));
        b.AfterNextTick(c => c.RemoveTarget());
        var correction = 0f;
        var lowestOnServer = 99f;
        net.Run(0.3, () =>
        {
            correction += Math.Abs(a.Session.TakeCorrection().Y);
            lowestOnServer = Math.Min(lowestOnServer, net.ServerPlayer(a).State.Position.Y);
        });
        a.Input = Idle;
        net.Run(1, () => correction += Math.Abs(a.Session.TakeCorrection().Y));

        Assert.Equal([BlockEditResult.Ok], b.LocalResults);
        Assert.Empty(b.Rejected);
        Assert.True(lowestOnServer < 4f, "Auf dem Server ist Anna ins Loch gefallen");
        Assert.True(correction > 0.1f, $"Annas Vorhersage wurde korrigiert (gesamt {correction})");
        Assert.True(SameState(net.ServerPlayer(a).State, a.State), $"Server {net.ServerPlayer(a).State.Position}, Client {a.State.Position}");
        Assert.False(a.Session.World.IsSolid(16, 3, 19));
    }

    [Fact]
    public void Interpolation_OtherPlayerMovesSmoothly_DelayedBehindServer()
    {
        var net = new SimulatedNetwork(seed: 3) { OneWayLatency = 0.04, Jitter = 0.02, Loss = 0.1 };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready && b.Session.RemotePlayers.Count == 1));
        net.Run(0.5);

        a.Input.Buttons = InputButtons.Forward;
        var seen = new List<float>();
        net.Run(2, () => seen.Add(b.Session.RemotePlayers[a.PlayerId].Current.Position.Z));
        a.Input = Idle;
        net.Run(0.5, () => seen.Add(b.Session.RemotePlayers[a.PlayerId].Current.Position.Z));

        var maxStep = 0f;
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i] >= seen[i - 1] - 1e-4f, $"Bild {i}: läuft rückwärts ({seen[i - 1]} → {seen[i]})");
            maxStep = Math.Max(maxStep, seen[i] - seen[i - 1]);
        }
        var perFrame = net.Content.Movement.WalkSpeed / 60f;
        Assert.True(maxStep < perFrame * 2.5f, $"größter Sprung {maxStep:0.000} bei {perFrame:0.000} pro Bild");
        Assert.Equal(net.ServerPlayer(a).State.Position.Z, seen[^1]); // am Ende genau dort, wo der Server sie hat
    }

    [Fact]
    public void Edit_AcceptedByServer_ReachesEveryone()
    {
        var net = new SimulatedNetwork { OneWayLatency = 0.05 };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));

        a.LookAt(new Vec3(16.5f, 4f, 19.5f));
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build));
        net.Frame();
        Assert.Equal(TestArena.Build, a.Session.World.GetBlock(16, 4, 19)); // sofort sichtbar
        Assert.False(b.Session.World.IsSolid(16, 4, 19));

        net.Run(0.3);
        Assert.Equal(TestArena.Build, net.Match.World.GetBlock(16, 4, 19));
        Assert.Equal(TestArena.Build, b.Session.World.GetBlock(16, 4, 19));
        Assert.Equal(TestArena.Build, a.Session.World.GetBlock(16, 4, 19));
        Assert.Equal(0, a.Session.Blocks.PendingCount);
    }

    [Fact]
    public void Edit_IntoOtherPlayer_RejectedAndRolledBack()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        net.Teleport(b, new Vec3(18.5f, 4, 18.5f)); // neben die Spawnzonen, dort darf gebaut werden
        net.Run(0.3);

        a.LookAt(new Vec3(18.5f, 4f, 18.5f));
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build));
        net.Frame();
        Assert.Equal([BlockEditResult.Ok], a.LocalResults); // der Client sieht kein Hindernis …
        Assert.Equal(TestArena.Build, a.Session.World.GetBlock(18, 4, 18));

        net.Run(0.3);
        Assert.Equal([BlockEditResult.BlockedByPlayer], a.Rejected); // … aber der Server
        Assert.False(a.Session.World.IsSolid(18, 4, 18));
        Assert.False(net.Match.World.IsSolid(18, 4, 18));
    }

    [Fact]
    public void Edit_TooFast_SecondRejected()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));

        // Ein manipulierter Client baut zwei Blöcke im selben Tick ab.
        a.AfterNextTick(c => c.Session.RequestRemove(16, 3, 18, 2));
        a.AfterNextTick(c => c.Session.RequestRemove(16, 3, 17, 2));
        a.LookAt(new Vec3(16.5f, 4f, 18.5f));
        net.Run(0.5);

        Assert.Contains(BlockEditResult.TooFast, a.Rejected);
        Assert.True(net.Match.World.IsSolid(16, 3, 17));
        Assert.True(a.Session.World.IsSolid(16, 3, 17));
    }

    [Fact]
    public void Edit_NotLookingAtBlock_NoLineOfSight()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));

        a.LookAt(new Vec3(16.5f, 4f, 19.5f));
        a.AfterNextTick(c => c.Session.RequestRemove(17, 3, 15, 1.5f)); // ein anderer Block in Reichweite
        net.Run(0.5);

        Assert.Equal([BlockEditResult.NoLineOfSight], a.Rejected);
        Assert.True(a.Session.World.IsSolid(17, 3, 15));
    }

    [Fact]
    public void Edit_BlockBehindAnother_NoLineOfSight()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));

        // Anna schaut auf den Boden; der Block darunter ist verdeckt.
        a.LookAt(new Vec3(16.5f, 4f, 18.5f));
        Assert.True(a.Target(out var hit));
        a.AfterNextTick(c => c.Session.RequestRemove(hit.X, hit.Y - 1, hit.Z, hit.Distance));
        net.Run(0.5);

        Assert.Equal([BlockEditResult.NoLineOfSight], a.Rejected);
    }

    [Fact]
    public void FallingGroup_SimulatedOnServer_InterpolatedOnClients_LandsIdentically()
    {
        var net = new SimulatedNetwork(seed: 5) { OneWayLatency = 0.05, Jitter = 0.02, Loss = 0.1 };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));

        // Anna baut den Verbindungsblock (13, 7, 16) von der Seite ab; der Balken (8…12, 7, 16) fällt.
        a.LookAt(new Vec3(14f, 7.5f, 16.5f));
        Assert.True(a.Target(out var hit));
        Assert.Equal((13, 7, 16), (hit.X, hit.Y, hit.Z));
        a.AfterNextTick(c => c.RemoveTarget());

        var startedOnB = net.RunUntil(() => b.Session.Falling.Groups.Count == 1, 1);
        Assert.True(startedOnB, "Ben sieht die fallende Gruppe");
        Assert.False(b.Session.World.IsSolid(10, 7, 16)); // Blöcke sind während des Falls nicht in der Welt
        var group = b.Session.Falling.Groups[0];
        Assert.Equal(5, group.Blocks.Count);

        var fallenSeen = new List<float>();
        net.RunUntil(() =>
        {
            if (b.Session.Falling.Groups.Count > 0) fallenSeen.Add(b.Session.Falling.Groups[0].Fallen);
            return b.Session.Falling.Groups.Count == 0;
        }, 3);

        Assert.True(fallenSeen.Count > 5, "mehrere Bilder lang zu sehen");
        for (var i = 1; i < fallenSeen.Count; i++) Assert.True(fallenSeen[i] >= fallenSeen[i - 1], "fällt nur nach unten");
        Assert.Empty(net.Match.Falling.Groups);
        for (var x = 8; x <= 12; x++)
        {
            Assert.Equal(TestArena.Build, net.Match.World.GetBlock(x, 4, 16));
            Assert.Equal(TestArena.Build, b.Session.World.GetBlock(x, 4, 16));
        }
        net.Run(0.3);
        AssertWorldsEqual(net.Match.World, a.Session.World);
        AssertWorldsEqual(net.Match.World, b.Session.World);
    }

    [Fact]
    public void LateJoiner_GetsAllChangesAndRunningFall()
    {
        var net = new SimulatedNetwork { OneWayLatency = 0.05 };
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));

        a.LookAt(new Vec3(16.5f, 4f, 19.5f));
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build));
        net.Run(0.3);
        a.LookAt(new Vec3(14f, 7.5f, 16.5f));
        a.AfterNextTick(c => c.RemoveTarget());
        Assert.True(net.RunUntil(() => net.Match.Falling.Groups.Count == 1, 1));
        net.Run(0.1); // die Gruppe fällt schon ein Stück

        var c = net.Join("Chris");
        Assert.True(net.RunUntil(() => c.Ready, 1));
        Assert.Equal(TestArena.Build, c.Session.World.GetBlock(16, 4, 19));
        Assert.False(c.Session.World.IsSolid(13, 7, 16));
        Assert.Single(c.Session.RemotePlayers);
        Assert.Equal("Anna", c.Session.RemotePlayers[a.PlayerId].Name);

        net.Run(2);
        Assert.Empty(c.Session.Falling.Groups);
        AssertWorldsEqual(net.Match.World, c.Session.World);
    }

    [Fact]
    public void PlayerLeft_DisappearsForOthers()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Session.RemotePlayers.Count == 1));
        net.Leave(b);
        Assert.True(net.RunUntil(() => a.Session.RemotePlayers.Count == 0, 1));
    }

    [Fact]
    public void Speedhack_SendingTooFast_IsLimitedToTickRate()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));
        var startTick = net.Match.Tick;
        a.TicksPerFrame = 2;
        a.Input.Buttons = InputButtons.Forward;
        net.Run(3);

        var server = net.ServerPlayer(a);
        var serverTicks = net.Match.Tick - startTick;
        var simulated = server.LastProcessedTick - (uint)server.DroppedInputs;
        Assert.True(simulated <= serverTicks + Match.MaxBudget, $"{simulated} Eingaben in {serverTicks} Ticks gerechnet");
        Assert.True(server.DroppedInputs > 0);
        // Gerechnet wurde höchstens eine Eingabe pro Tick: Anna ist auf dem Server nicht doppelt so weit.
        var maxDistance = net.Content.Movement.WalkSpeed * (serverTicks + Match.MaxBudget) / 60f;
        Assert.True(server.State.Position.Z - TestArena.SpawnA.Z <= maxDistance + 0.01f);
    }

    [Fact]
    public void BrokenMessage_IsReported()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.False(net.Match.HandleMessage(a.PlayerId, [1, 0, 0], 0, 3));
        Assert.False(net.Match.HandleMessage(a.PlayerId, [200], 0, 1));
        Assert.True(net.Match.HandleMessage(99, [1], 0, 1)); // unbekannter Spieler: ignorieren
    }

    [Fact]
    public void Snapshot_StaysBelowPacketLimit_WithManyPlayersAndGroups()
    {
        var sizes = new List<int>();
        var output = new RecordingOutput(sizes);
        var match = new Match(TestArena.Content, output, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new MatchSettings { PlayersPerTeam = ServerOptions.MaxPlayersPerTeam });
        for (var i = 0; i < ServerOptions.MaxPlayersLimit; i++) match.AddPlayer(Guid.NewGuid(), "Spieler" + i);
        // Viele kleine schwebende Gruppen: Säulen ohne Boden
        for (var x = 0; x < 60; x += 2)
            for (var z = 30; z < 60; z += 2)
                match.Falling.StartFalling([new GroupBlock(x, 20, z, TestArena.Stone)]);
        sizes.Clear();
        match.Step();
        match.Step();
        Assert.NotEmpty(sizes);
        Assert.All(sizes, s => Assert.True(s <= Match.MaxSnapshotBytes, $"Snapshot {s} Byte"));
    }

    private sealed class RecordingOutput(List<int> snapshotSizes) : IMatchOutput
    {
        public void Send(byte playerId, AoS2.Shared.Net.NetWriter message, AoS2.Shared.Net.Delivery delivery)
        {
            if (delivery == AoS2.Shared.Net.Delivery.Sequenced) snapshotSizes.Add(message.Length);
        }
    }

    private static float Vec3Distance(Vec3 a, Vec3 b) => (a - b).Length;
}
