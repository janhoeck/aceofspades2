using System.Security.Cryptography;
using System.Text;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class ClientPredictionTests
{
    private static readonly MovementSettings Settings = new();
    private static readonly PlayerInput Forward = new() { Buttons = InputButtons.Forward };

    [Fact]
    public void Step_IsTheSharedMovementCode()
    {
        var world = TestData.FlatWorld();
        var start = new PlayerState { Position = new Vec3(16, 4, 16), OnGround = true };
        var prediction = new ClientPrediction(world, Settings, start);
        var expected = start;
        for (var i = 0; i < 30; i++)
        {
            prediction.Step(Forward);
            expected = PlayerMovement.Step(expected, Forward, world, Settings);
        }
        Assert.Equal(30u, prediction.Tick);
        Assert.True(ClientPrediction.SameState(expected, prediction.State));
    }

    [Fact]
    public void Reconcile_WithMatchingServerState_ChangesNothing()
    {
        var world = TestData.FlatWorld();
        var prediction = new ClientPrediction(world, Settings, new PlayerState { Position = new Vec3(16, 4, 16), OnGround = true });
        var server = new ClientPrediction(world, Settings, prediction.State); // rechnet wie der Server
        for (var i = 0; i < 20; i++) prediction.Step(Forward);
        for (var i = 0; i < 12; i++) server.Step(Forward);

        var before = prediction.State;
        var correction = prediction.Reconcile(12, server.State);

        Assert.Equal(Vec3.Zero, correction);
        Assert.Equal(0, prediction.Mispredictions);
        Assert.True(ClientPrediction.SameState(before, prediction.State));
        Assert.Equal(12u, prediction.AckedTick);
        Assert.Equal(8u, prediction.UnackedCount);
    }

    [Fact]
    public void Reconcile_ServerDisagrees_ReplaysNewerInputsFromServerState()
    {
        var world = TestData.FlatWorld();
        var prediction = new ClientPrediction(world, Settings, new PlayerState { Position = new Vec3(16, 4, 16), OnGround = true });
        for (var i = 0; i < 20; i++) prediction.Step(Forward);

        // Der Server sagt: Nach Tick 10 stehst du zwei Blöcke weiter links (z. B. weggestoßen).
        var shifted = new PlayerState { Position = new Vec3(14, 4, 16.5f), OnGround = true };
        var correction = prediction.Reconcile(10, shifted);

        var expected = shifted;
        for (var i = 0; i < 10; i++) expected = PlayerMovement.Step(expected, Forward, world, Settings);
        Assert.True(ClientPrediction.SameState(expected, prediction.State));
        Assert.Equal(1, prediction.Mispredictions);
        Assert.True(correction.X < -1.9f, $"Korrektur {correction}");
        Assert.Equal(20u, prediction.Tick);
    }

    [Fact]
    public void Reconcile_WorldChangedMeanwhile_PlayerStopsAtNewWall()
    {
        var world = TestData.FlatWorld();
        var prediction = new ClientPrediction(world, Settings, new PlayerState { Position = new Vec3(16.5f, 4, 16.5f), OnGround = true });
        var acked = prediction.State;
        for (var i = 0; i < 60; i++) prediction.Step(Forward);
        Assert.True(prediction.State.Position.Z > 19);

        // Ein anderer Spieler hat eine Wand gebaut; der Server-Zustand bei Tick 0 ist unverändert, beim Neurechnen stößt man an.
        for (var y = 4; y < 7; y++)
            for (var x = 14; x < 19; x++)
                world.SetBlock(x, y, 18, TestData.Build);
        prediction.Reconcile(0, acked);

        Assert.Equal(18f - Settings.HalfWidth, prediction.State.Position.Z, 4);
    }

    [Fact]
    public void Reconcile_IgnoresStaleAndImpossibleAcks()
    {
        var world = TestData.FlatWorld();
        var prediction = new ClientPrediction(world, Settings, new PlayerState { Position = new Vec3(16, 4, 16), OnGround = true });
        for (var i = 0; i < 10; i++) prediction.Step(Forward);
        prediction.Reconcile(8, prediction.State);
        var state = prediction.State;

        Assert.Equal(Vec3.Zero, prediction.Reconcile(5, new PlayerState { Position = new Vec3(1, 4, 1) }));
        Assert.Equal(Vec3.Zero, prediction.Reconcile(11, new PlayerState { Position = new Vec3(1, 4, 1) }));
        Assert.True(ClientPrediction.SameState(state, prediction.State));
    }

    [Fact]
    public void InputPacket_ContainsUnackedInputs_UpToLimit()
    {
        var world = TestData.FlatWorld();
        var prediction = new ClientPrediction(world, Settings, new PlayerState { Position = new Vec3(16, 4, 16) });
        var packet = new InputPacket();

        prediction.FillInputPacket(packet);
        Assert.Empty(packet.Inputs); // noch kein Tick

        for (var i = 1; i <= 40; i++) prediction.Step(new PlayerInput { Yaw = i });
        prediction.FillInputPacket(packet);
        Assert.Equal(40u, packet.NewestTick);
        Assert.Equal(NetLimits.MaxInputsPerPacket, packet.Inputs.Count);
        Assert.Equal(40f, packet.Inputs[^1].Yaw);
        Assert.Equal(25f, packet.Inputs[0].Yaw);

        prediction.Reconcile(38, prediction.State);
        prediction.FillInputPacket(packet);
        Assert.Equal([39f, 40f], packet.Inputs.Select(i => i.Yaw));

        prediction.Reconcile(40, prediction.State);
        prediction.FillInputPacket(packet);
        Assert.Equal([40f], packet.Inputs.Select(i => i.Yaw)); // die neueste immer mitschicken
    }
}

public class InterpolationTests
{
    private static readonly Func<float, float, float, float> Lerp = (a, b, t) => a + (b - a) * t;

    [Fact]
    public void Sample_BetweenTwoEntries_Interpolates()
    {
        var buffer = new InterpolationBuffer<float>();
        buffer.Add(10, 0f);
        buffer.Add(12, 2f);
        buffer.Add(14, 10f);

        Assert.True(buffer.Sample(11, Lerp, out var v));
        Assert.Equal(1f, v, 5);
        Assert.True(buffer.Sample(13.5, Lerp, out v));
        Assert.Equal(8f, v, 5);
        Assert.True(buffer.Sample(5, Lerp, out v));
        Assert.Equal(0f, v); // vor dem ältesten: der älteste
        Assert.True(buffer.Sample(20, Lerp, out v));
        Assert.Equal(10f, v); // nach dem neuesten: keine Vorhersage
    }

    [Fact]
    public void Add_IgnoresOldOrDuplicateTicks_AndKeepsCapacity()
    {
        var buffer = new InterpolationBuffer<float>();
        Assert.False(buffer.Sample(0, Lerp, out _));
        for (uint t = 1; t <= 100; t++) buffer.Add(t, t);
        buffer.Add(50, -1f);
        buffer.Add(100, -1f);
        Assert.Equal(32, buffer.Count);
        Assert.Equal(100u, buffer.NewestTick);
        Assert.Equal(100f, buffer.Newest);
    }

    [Fact]
    public void SampleExtrapolated_ContinuesLastChange_Limited()
    {
        var buffer = new InterpolationBuffer<float>();
        buffer.Add(10, 0f);
        buffer.Add(12, 1f);
        Assert.True(buffer.SampleExtrapolated(13, Lerp, 4, out var v));
        Assert.Equal(1.5f, v, 5);
        Assert.True(buffer.SampleExtrapolated(100, Lerp, 4, out v));
        Assert.Equal(3f, v, 5);
    }

    [Fact]
    public void ServerClock_FollowsSnapshots_IgnoresLateOnes()
    {
        var clock = new ServerClock(60);
        // Snapshots alle 2 Ticks, 50 ms unterwegs, manche 40 ms zu spät
        var random = new Random(7);
        for (uint tick = 0; tick < 600; tick += 2)
        {
            var arrival = tick / 60.0 + 0.05 + (random.Next(4) == 0 ? 0.04 : 0);
            clock.OnSnapshot(tick + 1000, arrival);
        }
        // Zur Ankunftszeit eines pünktlichen Pakets zeigt die Uhr ungefähr dessen Tick.
        var estimate = clock.ServerTick(598 / 60.0 + 0.05);
        Assert.InRange(estimate, 1598 - 1.5, 1598 + 0.5);
    }

    [Fact]
    public void ServerClock_JumpsOnLargeDifference()
    {
        var clock = new ServerClock(60);
        clock.OnSnapshot(100, 10);
        clock.OnSnapshot(10_000, 10.1);
        Assert.Equal(10_000, clock.ServerTick(10.1), 3);
    }
}

public class PredictedBlocksTests
{
    [Fact]
    public void Accepted_StaysVisible()
    {
        var world = TestData.FlatWorld();
        var blocks = new PredictedBlocks(world);
        blocks.Predict(1, 5, 3, 5, BlockRegistry.Air);
        Assert.False(world.IsSolid(5, 3, 5));

        blocks.ApplyServer(5, 3, 5, BlockRegistry.Air); // Server bestätigt die Änderung …
        blocks.Resolve(1);                               // … und antwortet
        Assert.False(world.IsSolid(5, 3, 5));
        Assert.Equal(0, blocks.PendingCount);
    }

    [Fact]
    public void Rejected_IsUndone()
    {
        var world = TestData.FlatWorld();
        var blocks = new PredictedBlocks(world);
        blocks.Predict(1, 5, 4, 5, TestData.Build);
        Assert.Equal(TestData.Build, world.GetBlock(5, 4, 5));

        blocks.Resolve(1);
        Assert.False(world.IsSolid(5, 4, 5));
    }

    [Fact]
    public void ServerChangeWhilePending_WinsAfterAnswer()
    {
        var world = TestData.FlatWorld();
        var blocks = new PredictedBlocks(world);
        blocks.Predict(1, 5, 4, 5, TestData.Build);
        blocks.ApplyServer(5, 4, 5, TestData.Dirt); // ein anderer war schneller
        Assert.Equal(TestData.Build, world.GetBlock(5, 4, 5)); // Vorhersage bleibt sichtbar, bis die Antwort kommt

        blocks.Resolve(1); // abgelehnt
        Assert.Equal(TestData.Dirt, world.GetBlock(5, 4, 5));
    }

    [Fact]
    public void TwoPendingAtSamePosition_ResolveInOrder()
    {
        var world = TestData.FlatWorld();
        var blocks = new PredictedBlocks(world);
        blocks.Predict(1, 5, 3, 5, BlockRegistry.Air);   // abbauen
        blocks.Predict(2, 5, 3, 5, TestData.Build);      // gleich wieder bebauen
        blocks.ApplyServer(5, 3, 5, BlockRegistry.Air);
        blocks.Resolve(1);
        Assert.Equal(TestData.Build, world.GetBlock(5, 3, 5)); // zweite Vorhersage noch offen

        blocks.ApplyServer(5, 3, 5, TestData.Build);
        blocks.Resolve(2);
        Assert.Equal(TestData.Build, world.GetBlock(5, 3, 5));
        Assert.Equal(0, blocks.PendingCount);
    }

    [Fact]
    public void UnknownSeq_IsIgnored()
    {
        var blocks = new PredictedBlocks(TestData.FlatWorld());
        blocks.Resolve(99);
        Assert.Equal(0, blocks.PendingCount);
    }
}

public class ServerAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", ProtocolInfo.DefaultPort)]
    [InlineData(" localhost:4000 ", "localhost", 4000)]
    [InlineData("play.example.org:32887", "play.example.org", 32887)]
    [InlineData("[::1]:5000", "::1", 5000)]
    [InlineData("[::1]", "::1", ProtocolInfo.DefaultPort)]
    public void Parse_Valid(string text, string host, int port)
    {
        Assert.True(ServerAddress.TryParse(text, out var a));
        Assert.Equal(host, a.Host);
        Assert.Equal(port, a.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":32887")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:abc")]
    [InlineData("::1")]
    [InlineData("a b:1")]
    [InlineData("http://host")]
    public void Parse_Invalid(string text) => Assert.False(ServerAddress.TryParse(text, out _));

    [Fact]
    public void ToString_AlwaysWithPort()
    {
        ServerAddress.TryParse("localhost", out var a);
        Assert.Equal("localhost:32887", a.ToString());
        ServerAddress.TryParse("[::1]:7", out a);
        Assert.Equal("[::1]:7", a.ToString());
    }
}

public class DevJoinTicketTests
{
    [Fact]
    public void Ticket_IsJwt_SignedWithDevKey()
    {
        var account = Guid.NewGuid();
        var ticket = DevJoinTicket.Create(account, "Jan", new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var parts = ticket.Split('.');
        Assert.Equal(3, parts.Length);

        using var hmac = new HMACSHA256(DevJoinTicket.Key);
        var signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        Assert.Equal(Base64UrlDecode(parts[2]), signature);

        var payload = Json.JsonParser.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
        Assert.Equal(DevJoinTicket.Issuer, payload.RequireString("iss"));
        Assert.Equal(account.ToString(), payload.RequireString("sub"));
        Assert.Equal("Jan", payload.RequireString("name"));
        Assert.Equal(300, payload.RequireNumber("exp") - payload.RequireNumber("iat"));
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
