using AoS2.Shared;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;

namespace AoS2.Server.Tests;

/// <summary>
/// Kampf über das simulierte Netz: Server (<see cref="Match"/>) und zwei oder drei echte <see cref="ClientSession"/>s im selben
/// Prozess, mit Verzögerung. Lag-Ausgleich, Schaden, Tod und Respawn, Geschosse, Explosionen an Blöcken und Spielern.
/// </summary>
public class CombatNetTests
{
    private const byte Rifle = 1, Grenade = 2, Rocket = 3;

    private static (SimulatedNetwork Net, TestClient A, TestClient B) Duel(Vec3 posA, Vec3 posB, double latency = 0.05)
    {
        var net = new SimulatedNetwork { OneWayLatency = latency };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        net.Teleport(a, posA);
        net.Teleport(b, posB);
        net.Run(0.5);
        return (net, a, b);
    }

    /// <summary>Zielt vom Auge des Clients auf einen Punkt.</summary>
    private static void Aim(TestClient c, Vec3 target) => c.LookAt(target);

    private static List<ShotReport> Reports(SimulatedNetwork net)
    {
        var list = new List<ShotReport>();
        net.Match.ShotResolved += list.Add;
        return list;
    }

    // ---- Lag-Ausgleich ----

    /// <summary>
    /// Ben läuft quer vor Anna her. Anna hat 200 ms Ping und sieht Ben (Interpolation) gut 0,2 s in der Vergangenheit.
    /// Zielt sie auf das, was sie sieht, trifft sie: Der Server spult Ben auf genau diesen Zeitpunkt zurück.
    /// Zielt sie dorthin, wo Ben auf dem Server gerade wirklich ist, geht der Schuss daneben: Das hat sie nie gesehen.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LagCompensation_HitsWhatShooterSaw_NotWhereTargetIsNow(bool aimAtSeen)
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 8.5f), new Vec3(14.5f, 4, 24.5f), latency: 0.1);
        var reports = Reports(net);
        a.Input.Weapon = Rifle;
        b.Input.Buttons = InputButtons.Right; // Ben blickt nach +z, läuft also nach +x quer vor Anna her
        net.Run(0.8);

        var seen = a.Session.RemotePlayers[b.PlayerId].Current.Position;
        var actual = net.ServerPlayer(b).State.Position;
        Assert.True(actual.X - seen.X > 0.6f, $"Anna sieht Ben {actual.X - seen.X:0.00} Blöcke hinter seiner echten Position");

        var chest = new Vec3(0, 16f / 16, 0);
        Aim(a, (aimAtSeen ? seen : actual) + chest);
        a.FireTicks = 1;
        net.Run(0.5);

        var shot = Assert.Single(reports);
        if (aimAtSeen)
        {
            Assert.Equal(b.PlayerId, shot.Victim);
            Assert.Equal(HitZone.Torso, shot.Zone);
            // Der Server prüft genau die Position, die Anna gezeigt wurde (bis auf die Rundung des Zeitpunkts auf 1/256 Tick).
            Assert.True(net.Match.TryRewind(net.ServerPlayer(b), shot.ViewTick, out var rewound));
            Assert.True(Vec3.Distance(rewound.Position, a.SeenAtLastShot[b.PlayerId].Position) < 0.002f);
            Assert.Equal(75, net.ServerPlayer(b).Health);
            Assert.Equal(75, b.Session.Health);
            Assert.Contains(a.Events, e => e.Type == WorldEventType.PlayerDamaged && e.Attacker == a.PlayerId && e.PlayerId == b.PlayerId);
        }
        else
        {
            Assert.Null(shot.Victim);
            Assert.Equal(100, net.ServerPlayer(b).Health);
        }
        Assert.Contains(b.Events, e => e.Type == WorldEventType.ShotFired && e.PlayerId == a.PlayerId); // Ben sieht den Schuss
        Assert.DoesNotContain(a.Events, e => e.Type == WorldEventType.ShotFired); // Anna zeigt ihren eigenen Schuss selbst
        Assert.Equal(0, a.Session.Prediction.Mispredictions);
    }

    [Fact]
    public void LagCompensation_RewindIsLimited()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 8.5f), new Vec3(20.5f, 4, 24.5f));
        net.Match.Step();
        var serverB = net.ServerPlayer(b);
        // Ein Client behauptet, er sähe einen Zeitpunkt vor 10 s: Es wird höchstens maxRewindSeconds zurückgespult.
        Assert.True(net.Match.TryRewind(serverB, net.Match.Tick - 1 - TestArena.Combat.MaxRewindTicks, out _));
        Assert.False(serverB.TryGetView(net.Match.Tick - 1 - ServerPlayer.ViewHistorySize, out _));
    }

    // ---- Schaden, Tod, Respawn ----

    [Fact]
    public void Headshots_Kill_ThenRespawnAfterFiveSeconds_WithFullHealthAndAmmo()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 10.5f), new Vec3(20.5f, 4, 20.5f));
        var reports = Reports(net);
        a.Input.Weapon = Rifle;
        b.Input.Weapon = Rifle;
        net.Run(0.5);
        Aim(a, net.ServerPlayer(b).State.Position + new Vec3(0, 25f / 16, 0)); // Kopf
        a.FireTicks = 7; // zwei Schüsse (alle 6 Ticks)
        net.Run(0.5);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(HitZone.Head, r.Zone));
        var serverB = net.ServerPlayer(b);
        Assert.False(serverB.Alive);
        Assert.Equal(1, net.ServerPlayer(a).Kills);
        Assert.Equal(1, serverB.Deaths);
        var kill = Assert.Single(a.Events, e => e.Type == WorldEventType.PlayerKilled);
        Assert.Equal((b.PlayerId, a.PlayerId, Rifle, (byte)HitZone.Head), (kill.PlayerId, kill.Attacker, kill.Weapon, kill.Zone));
        Assert.False(b.Session.Alive);
        Assert.False(a.Session.RemotePlayers[b.PlayerId].Current.Alive);
        Assert.Equal(28, a.Session.Prediction.Weapons.GetAmmo(Rifle).Magazine);

        // Tot: nicht schießen, nicht bauen
        reports.Clear();
        b.FireTicks = 30;
        Aim(b, net.ServerPlayer(a).State.Position + new Vec3(0, 1, 0));
        net.Run(0.5);
        Assert.Empty(reports);
        Assert.Equal(100, net.ServerPlayer(a).Health);
        Assert.Equal(BlockEditResult.Dead, b.Session.RequestRemove(20, 3, 20, 1));

        // Nach 5 s wieder da: volle Lebenspunkte und Munition, an einem Spawnpunkt
        Assert.True(net.RunUntil(() => b.Session.Alive, 5));
        Assert.True(serverB.Alive);
        Assert.InRange(net.Match.Tick - serverB.RespawnAt, 0u, 60u);
        Assert.Equal(100, b.Session.Health);
        Assert.Contains(serverB.State.Position, new[] { TestArena.SpawnA, TestArena.SpawnB });
        Assert.Equal(30, serverB.Weapons.GetAmmo(Rifle).Magazine);
        net.Run(0.5);
        Assert.True(Vec3.Distance(b.State.Position, serverB.State.Position) < 0.01f, "Client ist mit zum Spawn gesprungen");
        var seen = a.Session.RemotePlayers[b.PlayerId].Current;
        Assert.True(seen.Alive);
        Assert.Equal(2, seen.Life); // erstes Leben beim Teambeitritt, zweites nach dem Respawn
        Assert.True(Vec3.Distance(seen.Position, serverB.State.Position) < 0.01f);
    }

    [Fact]
    public void BodyShots_ZoneFactors()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 10.5f), new Vec3(20.5f, 4, 20.5f));
        a.Input.Weapon = Rifle;
        net.Run(0.5);
        var feet = net.ServerPlayer(b).State.Position;

        Aim(a, feet + new Vec3(0, 16f / 16, 0)); // Rumpf ×1
        a.FireTicks = 1;
        net.Run(0.4);
        Assert.Equal(75, net.ServerPlayer(b).Health);

        Aim(a, feet + new Vec3(0.12f, 3f / 16, 0)); // Bein ×0,67 → 17
        a.FireTicks = 1;
        net.Run(0.4);
        Assert.Equal(75 - 17, net.ServerPlayer(b).Health);
        var damage = b.Events.Last(e => e.Type == WorldEventType.PlayerDamaged);
        Assert.Equal((byte)HitZone.Leg, damage.Zone);
        Assert.Equal(17, damage.Amount);
    }

    // ---- Geschosse und Explosionen ----

    [Fact]
    public void Rocket_DirectHit_Kills_AndBlowsCrater()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 10.5f), new Vec3(20.5f, 4, 20.5f));
        a.Input.Weapon = Rocket;
        net.Run(0.5);
        Aim(a, net.ServerPlayer(b).State.Position + new Vec3(0, 1, 0));
        a.FireTicks = 1;
        Assert.True(net.RunUntil(() => a.Events.Any(e => e.Type == WorldEventType.ProjectileSpawned), 1));
        Assert.Single(a.Session.Projectiles.All);
        Assert.True(net.RunUntil(() => a.Events.Any(e => e.Type == WorldEventType.ProjectileExploded), 2));
        net.Run(0.3);

        Assert.False(net.ServerPlayer(b).Alive);
        var kill = Assert.Single(b.Events, e => e.Type == WorldEventType.PlayerKilled);
        Assert.Equal((a.PlayerId, Rocket, WorldEvent.ExplosionZone), (kill.Attacker, kill.Weapon, kill.Zone));
        Assert.Equal(100, net.ServerPlayer(a).Health); // weit genug weg
        Assert.Empty(a.Session.Projectiles.All);
        Assert.Equal(0, a.Session.Prediction.Weapons.GetAmmo(Rocket).Magazine + a.Session.Prediction.Weapons.GetAmmo(Rocket).Reserve - 5);

        // Die Explosion hat Blöcke unter Ben zerstört; alle sehen dieselbe Welt.
        var world = net.Match.World;
        Assert.False(world.IsSolid(20, 3, 20));
        AssertSameWorld(world, a.Session.World);
        AssertSameWorld(world, b.Session.World);
    }

    [Fact]
    public void Rocket_AtPillar_DestroysBlocks_BeamFalls_SameEverywhere()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 18.5f), new Vec3(40.5f, 4, 40.5f)); // schräg, an den Spawnzonen (z 16…17) vorbei
        a.Input.Weapon = Rocket;
        net.Run(0.5);
        Aim(a, new Vec3(14f, 7.5f, 16.5f)); // Verbindungsblock (13, 7, 16) von vorn
        a.FireTicks = 1;
        Assert.True(net.RunUntil(() => net.Match.Falling.Groups.Count > 0, 2), "der Balken fällt");
        Assert.False(net.Match.World.IsSolid(13, 7, 16));
        Assert.True(net.RunUntil(() => net.Match.Falling.Groups.Count == 0 && a.Session.Falling.Groups.Count == 0, 3));
        net.Run(0.5);
        AssertSameWorld(net.Match.World, a.Session.World);
        AssertSameWorld(net.Match.World, b.Session.World);
        Assert.Equal(100, net.ServerPlayer(a).Health);
        Assert.Contains(a.Events, e => e.Type == WorldEventType.BlockDamaged); // Ränder des Kraters nur beschädigt
    }

    [Fact]
    public void Grenade_AtOwnFeet_ExplodesAfterFuse_SelfKill()
    {
        var (net, a, b) = Duel(new Vec3(20.5f, 4, 10.5f), new Vec3(40.5f, 4, 40.5f));
        a.Input.Weapon = Grenade;
        net.Run(0.5);
        a.Input.Pitch = 89; // senkrecht nach unten
        a.FireTicks = 1;
        net.Run(1);
        Assert.Single(net.Match.Projectiles);
        Assert.True(net.ServerPlayer(a).Alive, "Zündzeit noch nicht um");
        Assert.True(net.RunUntil(() => !net.ServerPlayer(a).Alive, 3));
        net.Run(0.2);
        var kill = Assert.Single(b.Events, e => e.Type == WorldEventType.PlayerKilled);
        Assert.Equal((a.PlayerId, a.PlayerId), (kill.PlayerId, kill.Attacker)); // Selbstmord: voller Schaden (selfDamage 1)
        Assert.Equal(0, net.ServerPlayer(a).Kills);
        Assert.Equal(2, net.ServerPlayer(a).Weapons.GetAmmo(Grenade).Magazine);
    }

    [Fact]
    public void Rifle_ShootsBlockToPieces_PillarTopFalls()
    {
        var (net, a, b) = Duel(new Vec3(17.5f, 4, 18.5f), new Vec3(40.5f, 4, 40.5f));
        a.Input.Weapon = Rifle;
        net.Run(0.5);
        Aim(a, new Vec3(14f, 5.5f, 16.5f)); // Säule (13, 5, 16)
        a.FireTicks = 1;
        net.Run(0.3);
        Assert.True(b.Session.BlockDamage.TryGetValue(new BlockPos(13, 5, 16), out var level));
        Assert.Equal((int)(15f / 100 * 255), level);
        Assert.True(net.Match.World.IsSolid(13, 5, 16));

        a.FireTicks = 6 * 6; // genug Schüsse für 100 Lebenspunkte
        Assert.True(net.RunUntil(() => !net.Match.World.IsSolid(13, 5, 16), 2));
        Assert.True(net.RunUntil(() => net.Match.Falling.Groups.Count > 0 || !net.Match.World.IsSolid(13, 7, 16), 1));
        a.FireTicks = 0;
        net.Run(2);
        AssertSameWorld(net.Match.World, b.Session.World);
        Assert.False(b.Session.BlockDamage.ContainsKey(new BlockPos(13, 5, 16)));
    }

    [Fact]
    public void LateJoiner_GetsBlockDamageAndFlyingProjectiles()
    {
        var (net, a, _) = Duel(new Vec3(17.5f, 4, 18.5f), new Vec3(40.5f, 4, 40.5f));
        a.Input.Weapon = Rifle;
        net.Run(0.5);
        Aim(a, new Vec3(14f, 5.5f, 16.5f));
        a.FireTicks = 1;
        net.Run(0.3);
        a.Input.Weapon = Rocket;
        net.Run(0.5);
        a.Input.Pitch = -89; // in den Himmel
        a.FireTicks = 1;
        net.Run(0.3);
        Assert.Single(net.Match.Projectiles);

        var c = net.Join("Carla");
        Assert.True(net.RunUntil(() => c.Ready, 1));
        Assert.True(c.Session.BlockDamage.ContainsKey(new BlockPos(13, 5, 16)));
        Assert.Single(c.Session.Projectiles.All);
        net.Run(0.1);
        var shown = c.Session.Projectiles.All.Values.Single().Position;
        Assert.True(Vec3.Distance(shown, net.Match.Projectiles.Single().Position) < 3, "Carla rechnet die Bahn ab dem Beitritt weiter");
    }

    [Fact]
    public void Shooting_WithLatencyJitterAndLoss_PredictionStaysExact()
    {
        var net = new SimulatedNetwork(seed: 7) { OneWayLatency = 0.06, Jitter = 0.03, Loss = 0.15 };
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        var frame = 0;
        net.Run(3, () =>
        {
            frame++;
            a.Input.Weapon = (byte)(frame < 120 ? Rifle : frame < 150 ? 0 : Rifle);
            a.Input.Yaw = 180 + frame % 90;
            a.Input.Pitch = 20;
            a.Input.Buttons = InputButtons.Forward | (frame % 40 < 25 ? InputButtons.Fire : 0) | (frame == 100 ? InputButtons.Reload : 0);
        });
        a.Input = default;
        net.Run(1);
        Assert.True(a.Fired.Count > 10);
        Assert.Equal(0, a.Session.Prediction.Mispredictions);
        Assert.True(a.Session.Prediction.Weapons.Equals(net.ServerPlayer(a).Weapons));
    }

    private static void AssertSameWorld(VoxelWorld expected, VoxelWorld actual)
    {
        for (var y = 0; y < expected.SizeY; y++)
            for (var z = 0; z < expected.SizeZ; z++)
                for (var x = 0; x < expected.SizeX; x++)
                    if (expected.GetBlock(x, y, z) != actual.GetBlock(x, y, z))
                        Assert.Fail($"Block ({x}, {y}, {z}): Server {expected.GetBlock(x, y, z)}, Client {actual.GetBlock(x, y, z)}");
    }
}
