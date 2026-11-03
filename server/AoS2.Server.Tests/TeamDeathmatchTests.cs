using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AoS2.Server.Tests;

/// <summary>
/// Team Deathmatch über das simulierte Netz (Server und echte <see cref="ClientSession"/>s in einem Prozess): Teamwahl und
/// Ausgleich, Punkte, Matchende, Neustart mit frischer Welt, Zeitlimit und die Regeln der Spawnzonen.
/// Test-Arena: Spawn Blau (16,5 | 4 | 16,5) in der Säule x 16…17, z 16…17; Spawn Grün (19,5 | 4 | 16,5) in x 19…20, z 16…17.
/// </summary>
public class TeamDeathmatchTests
{
    private const byte Rifle = 1, Rocket = 3;

    /// <summary>Freies Feld weit weg von Spawnzonen und Säule.</summary>
    private static readonly Vec3 Field = new(40.5f, 4, 40.5f);

    /// <summary>
    /// <paramref name="shooter"/> erschießt <paramref name="victim"/> mit dem Gewehr (Kopfschüsse) auf dem freien Feld.
    /// Liefert, ob das Opfer gestorben ist.
    /// </summary>
    private static bool Kill(SimulatedNetwork net, TestClient shooter, TestClient victim)
    {
        net.Teleport(shooter, Field);
        net.Teleport(victim, Field + new Vec3(0, 0, 6));
        shooter.Input.Weapon = Rifle;
        net.Run(0.5);
        shooter.LookAt(victim.State.Position + new Vec3(0, 1.75f, 0));
        var deathsBefore = net.ServerPlayer(victim).Deaths;
        shooter.FireTicks = 30;
        var died = net.RunUntil(() => net.ServerPlayer(victim).Deaths > deathsBefore, 2);
        shooter.FireTicks = 0;
        net.Run(0.3);
        return died;
    }

    private static void AssertSameWorld(VoxelWorld expected, VoxelWorld actual)
    {
        for (var y = 0; y < expected.SizeY; y++)
            for (var z = 0; z < expected.SizeZ; z++)
                for (var x = 0; x < expected.SizeX; x++)
                    if (expected.GetBlock(x, y, z) != actual.GetBlock(x, y, z))
                        Assert.Fail($"Block ({x}, {y}, {z}): erwartet {expected.GetBlock(x, y, z)}, ist {actual.GetBlock(x, y, z)}");
    }

    // ---- Teamwahl ----

    [Fact]
    public void NewPlayer_IsSpectator_UntilTeamChosen()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", team: null);
        Assert.True(net.RunUntil(() => a.Session.Phase == ClientPhase.Playing, 1));
        net.Run(6); // länger als die Respawn-Zeit: Zuschauer erscheinen nicht von selbst

        var server = net.ServerPlayer(a);
        Assert.Equal(Team.None, server.Team);
        Assert.False(server.Alive);
        Assert.Equal(Team.None, a.Session.Team);
        Assert.False(a.Session.Alive);
        Assert.Equal(0, a.Session.RespawnTicks);

        a.Session.RequestTeam(Team.Green);
        Assert.True(net.RunUntil(() => a.Ready, 1));
        Assert.Equal(Team.Green, server.Team);
        Assert.Equal(Team.Green, a.Session.Team);
        Assert.Equal(TestArena.SpawnB, a.State.Position);
        Assert.Equal(100, a.Session.Health);
    }

    [Fact]
    public void TeamChoice_KeepsTeamsBalanced_AndFull()
    {
        var net = new SimulatedNetwork(settings: new MatchSettings { PlayersPerTeam = 2 });
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", team: null);
        var c = net.Join("Carla", team: null);
        Assert.True(net.RunUntil(() => b.Session.Phase == ClientPhase.Playing && c.Session.Phase == ClientPhase.Playing));

        // Blau hat schon einen Spieler mehr: Ben darf nicht zu Blau.
        b.Session.RequestTeam(Team.Blue);
        Assert.True(net.RunUntil(() => b.MatchEvents.Any(e => e.Type == WorldEventType.TeamDenied), 1));
        Assert.Equal(TeamDenyReason.Unbalanced, b.MatchEvents.Last(e => e.Type == WorldEventType.TeamDenied).DenyReason);
        Assert.Equal(Team.None, net.ServerPlayer(b).Team);

        // Automatisch: ins kleinere Team (Grün).
        b.Session.RequestTeam(Team.None);
        Assert.True(net.RunUntil(() => b.Session.Team == Team.Green, 1));

        // Carla: Gleichstand, also darf sie wählen. Danach ist Blau voll (2 von 2).
        c.Session.RequestTeam(Team.Blue);
        Assert.True(net.RunUntil(() => c.Session.Team == Team.Blue, 1));
        Assert.Equal(2, net.Match.TeamSize(Team.Blue));

        // Ben will zu Blau wechseln: voll. Und ins eigene Team: schon drin.
        b.Session.RequestTeam(Team.Blue);
        b.Session.RequestTeam(Team.Green);
        net.Run(0.3);
        var denied = b.MatchEvents.Where(e => e.Type == WorldEventType.TeamDenied).Select(e => e.DenyReason).ToList();
        Assert.Equal([TeamDenyReason.Unbalanced, TeamDenyReason.Full, TeamDenyReason.SameTeam], denied);

        // Alle sehen dieselben Teams.
        Assert.Equal(Team.Blue, b.Session.RemotePlayers[a.PlayerId].Team);
        Assert.Equal(Team.Blue, a.Session.RemotePlayers[c.PlayerId].Team);
        Assert.Equal(Team.Green, a.Session.RemotePlayers[b.PlayerId].Team);
        Assert.Equal(2, a.Session.TeamSize(Team.Blue));
        Assert.Equal(1, c.Session.TeamSize(Team.Green));
    }

    [Fact]
    public void TeamSwitch_DiesWithoutDeath_RespawnsInNewTeamAfterRespawnTime()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        var c = net.Join("Carla", Team.Blue);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready && c.Ready));
        net.Teleport(c, Field);
        net.Run(0.2);

        c.Session.RequestTeam(Team.Green);
        Assert.True(net.RunUntil(() => c.Session.Team == Team.Green, 1));
        net.Run(0.2);
        var server = net.ServerPlayer(c);
        Assert.False(server.Alive);
        Assert.False(c.Session.Alive);
        Assert.Equal(0, server.Deaths);
        Assert.DoesNotContain(a.Events, e => e.Type == WorldEventType.PlayerKilled);

        Assert.True(net.RunUntil(() => c.Session.Alive, 5.5));
        net.Run(0.3);
        Assert.Equal(TestArena.SpawnB, server.State.Position);
        Assert.True(Vec3.Distance(c.State.Position, TestArena.SpawnB) < 0.01f);
    }

    // ---- Punkte ----

    [Fact]
    public void EnemyKill_ScoresForTeam_TeammatesCannotBeHurt()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        var c = net.Join("Carla", Team.Blue);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready && c.Ready));

        // Teambeschuss ist aus: Anna trifft Carla nicht (der Schuss geht durch).
        Assert.False(Kill(net, a, c));
        Assert.Equal(100, net.ServerPlayer(c).Health);
        Assert.Equal(0, net.Match.Info.ScoreBlue);

        Assert.True(Kill(net, a, b));
        Assert.Equal(1, net.Match.Info.ScoreBlue);
        Assert.Equal(0, net.Match.Info.ScoreGreen);
        Assert.Equal((1, 0), (net.ServerPlayer(a).Kills, net.ServerPlayer(a).Deaths));
        Assert.Equal((0, 1), (net.ServerPlayer(b).Kills, net.ServerPlayer(b).Deaths));

        // Alle Clients kennen Stand und Statistik.
        foreach (var client in new[] { a, b, c }) Assert.Equal(1, client.Session.Match.ScoreBlue);
        Assert.Equal(1, b.Session.RemotePlayers[a.PlayerId].Kills);
        Assert.Equal(1, c.Session.RemotePlayers[b.PlayerId].Deaths);
        Assert.Equal(1, a.Session.Kills);
        Assert.Equal(1, b.Session.Deaths);

        // Wer später kommt, sieht dasselbe.
        var d = net.Join("Dora", team: null);
        Assert.True(net.RunUntil(() => d.Session.Phase == ClientPhase.Playing, 1));
        Assert.Equal(1, d.Session.Match.ScoreBlue);
        Assert.Equal((Team.Blue, 1), (d.Session.RemotePlayers[a.PlayerId].Team, d.Session.RemotePlayers[a.PlayerId].Kills));
        Assert.Equal(1, d.Session.RemotePlayers[b.PlayerId].Deaths);
    }

    [Fact]
    public void FriendlyFireOn_TeamkillCountsNothing()
    {
        var net = new SimulatedNetwork(settings: new MatchSettings { FriendlyFire = true });
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        var c = net.Join("Carla", Team.Blue);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready && c.Ready));
        Assert.True(a.Session.Match.FriendlyFire);

        Assert.True(Kill(net, a, c));
        Assert.Equal(0, net.Match.Info.ScoreBlue + net.Match.Info.ScoreGreen);
        Assert.Equal(0, net.ServerPlayer(a).Kills);
        Assert.Equal(1, net.ServerPlayer(c).Deaths);
    }

    [Fact]
    public void ScoreLimit_EndsMatch_FreezesEveryone_ThenRestartsWithFreshWorld()
    {
        var net = new SimulatedNetwork(settings: new MatchSettings { ScoreLimit = 2, MatchEndSeconds = 2 });
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        var original = net.Content.LoadMap().World;

        // Die Welt verändern: Anna baut den Verbindungsblock ab, der Balken fällt.
        a.LookAt(new Vec3(14f, 7.5f, 16.5f));
        a.AfterNextTick(c => c.RemoveTarget());
        net.Run(2);
        Assert.False(net.Match.World.IsSolid(13, 7, 16));
        Assert.True(net.Match.ChangeCount > 0);

        Assert.True(Kill(net, a, b));
        Assert.Equal(MatchPhase.Playing, net.Match.Info.Phase);
        Assert.True(net.RunUntil(() => b.Session.Alive, 5.5));
        Assert.True(Kill(net, a, b));

        // 2 : 0 – Blau gewinnt, alle sehen das Ergebnis.
        Assert.Equal(MatchPhase.Ended, net.Match.Info.Phase);
        Assert.Equal(Team.Blue, net.Match.Info.Winner);
        Assert.True(a.Session.MatchOver);
        Assert.Equal((MatchPhase.Ended, Team.Blue, 2), (b.Session.Match.Phase, b.Session.Match.Winner, b.Session.Match.ScoreBlue));

        // Nach dem Matchende: niemand schießt, baut oder läuft.
        var firedBefore = a.Fired.Count;
        var reports = new List<ShotReport>();
        net.Match.ShotResolved += reports.Add;
        a.FireTicks = 30;
        a.Input.Buttons = InputButtons.Forward;
        var before = net.ServerPlayer(a).State.Position;
        net.Run(0.5);
        Assert.Equal(firedBefore, a.Fired.Count);
        Assert.Empty(reports);
        Assert.Equal(before.X, net.ServerPlayer(a).State.Position.X);
        Assert.Equal(before.Z, net.ServerPlayer(a).State.Position.Z);
        a.Input = default;
        a.LookAt(new Vec3(a.State.Position.X + 0.5f, 3.9f, a.State.Position.Z + 2));
        Assert.True(a.Target(out _));
        Assert.Equal(BlockEditResult.MatchOver, a.RemoveTarget());

        // Nach 2 s: neues Match. Frische Welt überall, Punkte und Statistik auf 0, alle wieder an ihrem Spawn.
        Assert.True(net.RunUntil(() => net.Match.Info.Phase == MatchPhase.Playing, 2.5));
        net.Run(0.5);
        Assert.Equal(2, net.Match.Info.MatchNumber);
        Assert.Equal(0, net.Match.Info.ScoreBlue);
        Assert.Equal(0, net.Match.ChangeCount);
        AssertSameWorld(original, net.Match.World);
        AssertSameWorld(original, a.Session.World);
        AssertSameWorld(original, b.Session.World);
        Assert.Empty(net.Match.Falling.Groups);
        Assert.Empty(b.Session.Falling.Groups);
        Assert.False(a.Session.MatchOver);
        Assert.Equal(0, a.Session.Match.ScoreBlue);
        Assert.Equal((0, 0), (a.Session.Kills, b.Session.Deaths));
        Assert.Equal(0, b.Session.RemotePlayers[a.PlayerId].Kills);
        Assert.Equal((0, 0), (net.ServerPlayer(a).Kills, net.ServerPlayer(b).Deaths));
        Assert.True(a.Session.Alive && b.Session.Alive);
        Assert.Equal(TestArena.SpawnA, net.ServerPlayer(a).State.Position);
        Assert.Equal(TestArena.SpawnB, net.ServerPlayer(b).State.Position);
        Assert.True(Vec3.Distance(a.State.Position, TestArena.SpawnA) < 0.01f);
        Assert.Equal(Team.Green, net.ServerPlayer(b).Team); // Teams bleiben

        // Und es geht weiter: Kills zählen wieder.
        Assert.True(Kill(net, b, a));
        Assert.Equal(1, net.Match.Info.ScoreGreen);
    }

    [Theory]
    [InlineData(1, 0, Team.Blue)]
    [InlineData(0, 0, Team.None)]
    public void TimeLimit_LeaderWins_OrDraw(int blueKills, int greenKills, Team winner)
    {
        var output = new NullOutput();
        var match = new Match(TestArena.Content, output, NullLogger.Instance, new MatchSettings { TimeLimitMinutes = 1, MatchEndSeconds = 1 });
        // Ohne Spieler in Teams läuft die Uhr nicht.
        for (var i = 0; i < 4000; i++) match.Step();
        Assert.Equal(MatchPhase.Playing, match.Info.Phase);

        var blue = match.AddPlayer(Guid.NewGuid(), "Blau");
        var green = match.AddPlayer(Guid.NewGuid(), "Grün");
        match.SelectTeam(blue, Team.Blue);
        match.SelectTeam(green, Team.Green);
        // Aus den Spawnzonen heraus, sonst gibt es keinen Schaden.
        blue.State = new PlayerState { Position = new Vec3(30.5f, 4, 30.5f) };
        green.State = new PlayerState { Position = new Vec3(34.5f, 4, 30.5f) };
        var end = match.Info.PhaseEndTick;
        Assert.InRange(end - match.Tick, 3590u, 3601u);
        for (var i = 0; i < blueKills; i++) match.Damage(green, blue.Id, 1000, Rifle, 0);
        for (var i = 0; i < greenKills; i++) match.Damage(blue, green.Id, 1000, Rifle, 0);

        while (match.Tick < end - 1) match.Step();
        Assert.Equal(MatchPhase.Playing, match.Info.Phase);
        match.Step();
        Assert.Equal(MatchPhase.Ended, match.Info.Phase);
        Assert.Equal(winner, match.Info.Winner);
        for (var i = 0; i < 61; i++) match.Step();
        Assert.Equal(MatchPhase.Playing, match.Info.Phase);
        Assert.Equal(2, match.Info.MatchNumber);
    }

    // ---- Spawnzonen ----

    [Fact]
    public void SpawnZone_EnemyStopsAtInvisibleWall_ClientPredictsIt()
    {
        var net = new SimulatedNetwork { OneWayLatency = 0.05 };
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        net.Teleport(a, Field);
        net.Teleport(b, new Vec3(14.5f, 4, 16.5f));
        net.Run(0.5);
        var mispredictions = b.Session.Prediction.Mispredictions;

        b.Input = new PlayerInput { Buttons = InputButtons.Forward, Yaw = 90 }; // nach +x, auf den blauen Spawn zu
        net.Run(1.5);
        Assert.Equal(16 - TestArena.Movement.HalfWidth, net.ServerPlayer(b).State.Position.X, 0.001f);
        Assert.True(ClientPrediction.SameState(net.ServerPlayer(b).State, b.Session.Prediction.State) || b.Session.Prediction.UnackedCount > 0);
        Assert.Equal(16 - TestArena.Movement.HalfWidth, b.State.Position.X, 0.001f);
        Assert.Equal(mispredictions, b.Session.Prediction.Mispredictions);

        // Anna (Blau) läuft denselben Weg: durch ihren Spawn hindurch bis an die Wand des grünen Spawns (x = 19).
        net.Teleport(a, new Vec3(14.5f, 4, 16.5f));
        net.Teleport(b, Field);
        net.Run(0.3);
        a.Input = new PlayerInput { Buttons = InputButtons.Forward, Yaw = 90 };
        net.Run(1.5);
        Assert.Equal(19 - TestArena.Movement.HalfWidth, net.ServerPlayer(a).State.Position.X, 0.001f);
    }

    [Fact]
    public void SpawnZone_NoShootingOut_NoHitsIn()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        var reports = new List<ShotReport>();
        net.Match.ShotResolved += reports.Add;

        // Anna steht im Spawn und zielt auf Ben: Die Waffe feuert nicht (Client und Server).
        net.Teleport(b, new Vec3(16.5f, 4, 22.5f));
        a.Input.Weapon = Rifle;
        net.Run(0.5);
        a.LookAt(net.ServerPlayer(b).State.Position + new Vec3(0, 1, 0));
        a.FireTicks = 30;
        net.Run(0.8);
        Assert.Empty(a.Fired);
        Assert.Empty(reports);
        Assert.Equal(100, net.ServerPlayer(b).Health);

        // Ben schießt in den Spawn hinein: Der Schuss endet an der Zonengrenze (z = 17), Anna bleibt heil.
        b.Input.Weapon = Rifle;
        net.Run(0.5);
        b.LookAt(net.ServerPlayer(a).State.Position + new Vec3(0, 1, 0));
        b.FireTicks = 12;
        net.Run(0.8);
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Null(r.Victim));
        Assert.All(reports, r => Assert.Equal(17f, r.To.Z, 0.01f));
        Assert.Equal(100, net.ServerPlayer(a).Health);

        // Eine Rakete explodiert an der Zonengrenze; wer im Spawn steht, bekommt nichts ab.
        b.Input.Weapon = Rocket;
        net.Run(0.5);
        b.LookAt(net.ServerPlayer(a).State.Position + new Vec3(0, 0.5f, 0));
        b.FireTicks = 1;
        Assert.True(net.RunUntil(() => b.Events.Any(e => e.Type == WorldEventType.ProjectileExploded), 2));
        var boom = b.Events.Last(e => e.Type == WorldEventType.ProjectileExploded);
        Assert.InRange(boom.Position.Z, 17f, 17.1f);
        net.Run(0.2);
        Assert.Equal(100, net.ServerPlayer(a).Health);
    }

    [Fact]
    public void SpawnZone_BlockEdits_RejectedByClientAndServer()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue);
        var b = net.Join("Ben", Team.Green);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        net.Teleport(b, new Vec3(16.5f, 4, 19.5f));
        net.Run(0.5);

        // Ben schaut auf den Boden im blauen Spawn: In die Zone (16, 4, 16) bauen geht nicht.
        b.LookAt(new Vec3(16.5f, 4f, 16.6f));
        net.Run(0.1); // die Blickrichtung muss beim Server angekommen sein
        Assert.True(b.Target(out var hit));
        Assert.Equal((16, 4, 16), (hit.PlaceX, hit.PlaceY, hit.PlaceZ));
        Assert.Equal(BlockEditResult.ProtectedZone, b.PlaceAtTarget(TestArena.Build));

        // Ein manipulierter Client schickt die Anfrage trotzdem: Der Server lehnt ab.
        var request = new BlockEditRequest { Seq = 77, Tick = b.Session.Prediction.Tick, Kind = BlockEditKind.Place, X = 16, Y = 4, Z = 16, BlockId = TestArena.Build };
        var w = new NetWriter(32);
        request.Write(w);
        ((IMessageSender)b).Send(w, Delivery.ReliableOrdered);
        net.Run(0.5);
        Assert.Equal([BlockEditResult.ProtectedZone], b.Rejected);
        Assert.False(net.Match.World.IsSolid(16, 4, 16));
    }

    private sealed class NullOutput : IMatchOutput
    {
        public void Send(byte playerId, NetWriter message, Delivery delivery)
        {
        }
    }
}
