using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

/// <summary>
/// Regeln der Zonen (M6): unsichtbare Wände für Gegner im gemeinsamen Bewegungscode, kein Feuern im Spawn, Geschosse und
/// Strahlen halten an der Zonengrenze, geschützte Blöcke (auch bei Explosionen und fallenden Gruppen).
/// Welt: flach, man steht auf y = 4. Spawnzone Blau x/z 10…20, Grün x/z 40…50 (je über die ganze Höhe), Kampfzone z 22…38.
/// </summary>
public class ZoneTests
{
    private static readonly MovementSettings Movement = new();

    private static readonly CombatSettings Combat = CombatSettings.Parse(
        File.ReadAllText(TestData.RepoFile("data/combat.json")), File.ReadAllText(TestData.RepoFile("data/weapons.json")), 60);

    private static WeaponSettings Rocket => Combat.Weapons.Single(w => w.Name == "rocket");
    private static WeaponSettings Grenade => Combat.Weapons.Single(w => w.Name == "grenade");

    private static MapMetadata Metadata(bool combatZone = false)
    {
        var meta = new MapMetadata { Name = "Zonen" };
        meta.Teams[Team.Blue] = new TeamSpawn { Zone = new BlockBox(10, 0, 10, 20, 32, 20), Points = { new Vec3(15.5f, 4, 15.5f) } };
        meta.Teams[Team.Green] = new TeamSpawn { Zone = new BlockBox(40, 0, 40, 50, 32, 50), Points = { new Vec3(45.5f, 4, 45.5f), new Vec3(46.5f, 4, 45.5f) } };
        if (combatZone) meta.CombatZone = new BlockBox(0, 0, 22, 64, 32, 38);
        return meta;
    }

    private static MapZones Zones(bool combatZone = false) => new(Metadata(combatZone));

    private static VoxelWorld World() => TestData.FlatWorld(64, 32, 64);

    /// <summary>Läuft <paramref name="ticks"/> Ticks in Richtung +x (Blick nach +x, vorwärts).</summary>
    private static PlayerState Walk(PlayerState s, VoxelWorld world, BlockBox[]? barriers, int ticks)
    {
        var input = new PlayerInput { Buttons = InputButtons.Forward, Yaw = 90 };
        for (var i = 0; i < ticks; i++) s = PlayerMovement.Step(s, input, world, Movement, barriers);
        return s;
    }

    // ---- Zonen ----

    [Fact]
    public void BlockRules_SpawnProtected_OnlyCombatZoneEditable()
    {
        var zones = Zones();
        Assert.False(zones.CanEditBlock(15, 3, 15)); // Spawn Blau
        Assert.False(zones.CanEditBlock(45, 10, 45)); // Spawn Grün
        Assert.True(zones.CanEditBlock(30, 3, 30)); // ohne Kampfzone überall sonst

        var withCombat = Zones(combatZone: true);
        Assert.True(withCombat.CanEditBlock(30, 3, 30));
        Assert.False(withCombat.CanEditBlock(30, 3, 21)); // zwischen Spawn und Kampfzone
        Assert.True(withCombat.IsAnchor(30, 3, 21));
        Assert.False(MapZones.None.IsAnchor(15, 3, 15));
    }

    [Fact]
    public void SpawnZoneAt_AndBarriers_PerTeam()
    {
        var zones = Zones();
        Assert.Equal(Team.Blue, zones.SpawnZoneAt(new Vec3(15.5f, 4, 15.5f)));
        Assert.Equal(Team.Green, zones.SpawnZoneAt(new Vec3(40.01f, 4, 49.9f)));
        Assert.Equal(Team.None, zones.SpawnZoneAt(new Vec3(20f, 4, 15f))); // Max gehört nicht dazu
        Assert.True(zones.InSpawnZone(new Vec3(12, 4, 12)));
        Assert.Equal(new BlockBox(40, 0, 40, 50, 32, 50), Assert.Single(zones.BarriersFor(Team.Blue)));
        Assert.Equal(new BlockBox(10, 0, 10, 20, 32, 20), Assert.Single(zones.BarriersFor(Team.Green)));
        Assert.Empty(zones.BarriersFor(Team.None));
    }

    [Fact]
    public void Raycast_StopsAtZoneBorder_IgnoresZoneAroundOrigin()
    {
        var zones = Zones();
        Assert.True(zones.Raycast(new Vec3(5, 6, 15), new Vec3(1, 0, 0), 100, out var d, out var nx, out var ny, out var nz));
        Assert.Equal(5f, d, 0.001f);
        Assert.Equal((-1, 0, 0), (nx, ny, nz));

        Assert.False(zones.Raycast(new Vec3(5, 6, 15), new Vec3(1, 0, 0), 4.9f, out _, out _, out _, out _)); // zu kurz
        Assert.False(zones.Raycast(new Vec3(5, 6, 25), new Vec3(1, 0, 0), 100, out _, out _, out _, out _)); // daneben

        // Aus der blauen Zone heraus: die eigene zählt nicht, die grüne (diagonal) schon.
        var dir = new Vec3(1, 0, 1).Normalized;
        Assert.True(zones.Raycast(new Vec3(15, 6, 15), dir, 100, out d, out _, out _, out _));
        Assert.Equal(25 * MathF.Sqrt(2), d, 0.01f);
    }

    [Fact]
    public void Spawns_PickPerTeam_SpectatorsInTheMiddle()
    {
        var world = World();
        var meta = Metadata();
        Assert.Equal(new Vec3(15.5f, 4, 15.5f), Spawns.Pick(world, meta, Team.Blue, 7));
        Assert.Equal(new Vec3(46.5f, 4, 45.5f), Spawns.Pick(world, meta, Team.Green, 1));
        Assert.Equal(new Vec3(45.5f, 4, 45.5f), Spawns.Pick(world, meta, Team.Green, 2));
        Assert.Equal(new Vec3(32.5f, 4, 32.5f), Spawns.Pick(world, meta, Team.None, 0));
    }

    // ---- Bewegung ----

    [Fact]
    public void Enemy_IsStoppedByInvisibleWall_OwnTeamWalksIn()
    {
        var world = World();
        var zones = Zones();
        var start = new PlayerState { Position = new Vec3(5.5f, 4, 15.5f), OnGround = true };

        var green = Walk(start, world, zones.BarriersFor(Team.Green), 120);
        Assert.Equal(10 - Movement.HalfWidth, green.Position.X, 0.001f); // genau an der Zonengrenze
        Assert.Equal(0, green.Velocity.X);

        var blue = Walk(start, world, zones.BarriersFor(Team.Blue), 120);
        Assert.True(blue.Position.X > 14, $"Blau läuft in den eigenen Spawn: {blue.Position}");

        // Ohne Wände (z. B. offline) wie bisher.
        Assert.Equal(blue.Position, Walk(start, world, null, 120).Position);
    }

    [Fact]
    public void Enemy_CannotJumpOrFallIn_ZoneIsSolidFromAbove()
    {
        var world = World();
        // Ein Gegner über der Zone (z. B. auf einem gebauten Turm) steht auf ihr wie auf einem Dach.
        var above = new PlayerState { Position = new Vec3(15.5f, 33, 15.5f) };
        var zones = new MapZones(Metadata());
        var s = above;
        for (var i = 0; i < 60; i++) s = PlayerMovement.Step(s, default, world, Movement, zones.BarriersFor(Team.Green));
        Assert.Equal(32f, s.Position.Y, 0.001f);
        Assert.True(s.OnGround);
    }

    [Fact]
    public void Enemy_AlreadyInside_CanWalkOut_ButNotBackIn()
    {
        var world = World();
        var barriers = Zones().BarriersFor(Team.Green);
        var inside = new PlayerState { Position = new Vec3(18.5f, 4, 15.5f), OnGround = true };

        var outside = Walk(inside, world, barriers, 60);
        Assert.True(outside.Position.X > 21, $"aus der Zone heraus: {outside.Position}");

        var back = new PlayerInput { Buttons = InputButtons.Forward, Yaw = 270 };
        var s = outside;
        for (var i = 0; i < 120; i++) s = PlayerMovement.Step(s, back, world, Movement, barriers);
        Assert.Equal(20 + Movement.HalfWidth, s.Position.X, 0.001f);
    }

    [Fact]
    public void Prediction_UsesTeamWalls_SameAsServerCode()
    {
        var world = World();
        var zones = Zones();
        var start = new PlayerState { Position = new Vec3(5.5f, 4, 15.5f), OnGround = true };
        var client = new ClientPrediction(world, Movement, start, Combat, zones) { Team = Team.Green };
        var input = new PlayerInput { Buttons = InputButtons.Forward, Yaw = 90 };
        for (var i = 0; i < 120; i++) client.Step(input);
        Assert.Equal(Walk(start, world, zones.BarriersFor(Team.Green), 120).Position, client.State.Position);
    }

    // ---- Waffen ----

    [Fact]
    public void Weapons_BlockedInSpawn_FireAgainOutside()
    {
        var w = WeaponState.Full(Combat, 1);
        var fire = new PlayerInput { Buttons = InputButtons.Fire, Weapon = 1 };
        for (var i = 0; i < 60; i++) Assert.Equal(0, WeaponSim.Step(ref w, fire, Combat, blocked: true));
        Assert.Equal(30, w.GetAmmo(1).Magazine);
        Assert.Equal(1, WeaponSim.Step(ref w, fire, Combat, blocked: false));

        // Nachladen und Wechseln gehen auch im Spawn.
        WeaponSim.Step(ref w, new PlayerInput { Buttons = InputButtons.Reload, Weapon = 1 }, Combat, blocked: true);
        Assert.True(w.Reload > 0);
    }

    [Fact]
    public void Prediction_NoShotsWhileStandingInSpawn()
    {
        var world = World();
        var start = new PlayerState { Position = new Vec3(15.5f, 4, 15.5f), OnGround = true };
        var client = new ClientPrediction(world, Movement, start, Combat, Zones()) { Team = Team.Blue };
        var shots = 0;
        // Feuern und dabei aus dem Spawn laufen (+x): Schüsse erst ab der Zonengrenze x = 20.
        for (var i = 0; i < 90; i++)
        {
            client.Step(new PlayerInput { Buttons = InputButtons.Fire | InputButtons.Forward, Yaw = 90, Weapon = 1 }, out var fired);
            if (fired == 0) continue;
            shots++;
            Assert.True(client.State.Position.X >= 20, $"Schuss im Spawn bei {client.State.Position}");
        }
        Assert.True(shots > 0);
    }

    [Fact]
    public void Rocket_ExplodesAtZoneWall()
    {
        var p = new Projectile { Position = new Vec3(3.5f, 6.5f, 15.5f), Velocity = new Vec3(Rocket.Speed, 0, 0) };
        var ticks = 0;
        while (!ProjectileSim.Step(ref p, Rocket, World(), 1 / 60f, Zones())) Assert.True(++ticks < 100);
        Assert.InRange(p.Position.X, 9.9f, 10f);
    }

    [Fact]
    public void Grenade_BouncesOffZoneWall()
    {
        var p = new Projectile { Position = new Vec3(8.5f, 6, 15.5f), Velocity = new Vec3(10, 0, 0) };
        for (var i = 0; i < 30; i++) ProjectileSim.Step(ref p, Grenade, World(), 1 / 60f, Zones());
        Assert.True(p.Position.X < 10, "nicht in den Spawn");
        Assert.True(p.Velocity.X <= 0, "zurückgeprallt");
    }

    [Fact]
    public void Explosion_AtZoneBorder_SparesProtectedBlocks()
    {
        var world = World();
        var damage = new BlockDamage(world, TestData.Registry);
        var destroyed = new List<BlockPos>();
        var damaged = new List<BlockPos>();
        var center = new Vec3(10f, 4.1f, 15.5f); // auf dem Boden genau an der Grenze
        Explosions.DamageBlocks(world, damage, center, Rocket.Explosion.BlockRadius, Rocket.Explosion.BlockDamage, destroyed, damaged, Zones());
        Assert.NotEmpty(destroyed);
        Assert.All(destroyed.Concat(damaged), b => Assert.True(b.X < 10, $"Block {b} liegt im Spawn"));
    }

    // ---- Blöcke ----

    [Fact]
    public void BlockEditing_InSpawnOrOutsideCombatZone_Protected()
    {
        var world = World();
        var r = TestData.Registry;
        var zones = Zones(combatZone: true);
        var player = new PlayerState { Position = new Vec3(30.5f, 4, 30.5f) };
        Assert.Equal(BlockEditResult.ProtectedZone, BlockEditing.CanRemove(world, r, 15, 3, 15, 2, zones));
        Assert.Equal(BlockEditResult.ProtectedZone, BlockEditing.CanRemove(world, r, 30, 3, 21, 2, zones));
        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanRemove(world, r, 30, 3, 30, 2, zones));
        Assert.Equal(BlockEditResult.ProtectedZone, BlockEditing.CanPlace(world, r, 15, 4, 15, TestData.Build, 2, player, Movement, zones));
        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanPlace(world, r, 30, 4, 32, TestData.Build, 2, player, Movement, zones));
        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanRemove(world, r, 15, 3, 15, 2)); // ohne Zonen wie bisher
    }

    [Fact]
    public void FallingGroups_HangingOnProtectedBlocks_StayAnchored()
    {
        var world = World();
        // Säule (8, 4…7, 15) trägt einen Balken (8…12, 8, 15), der bis in den blauen Spawn (x ≥ 10) reicht.
        for (var y = 4; y <= 7; y++) world.SetBlock(8, y, 15, TestData.Stone);
        for (var x = 8; x <= 12; x++) world.SetBlock(x, 8, 15, TestData.Build);
        world.SetBlock(8, 5, 15, BlockRegistry.Air);

        Assert.Single(FloatingBlocks.FindDetached(world, 8, 5, 15)); // ohne Zonen fällt der Balken mit der Säule
        Assert.Empty(FloatingBlocks.FindDetached(world, 8, 5, 15, isAnchor: Zones().IsAnchor));

        var falling = new FallingBlocks(world) { Anchor = Zones().IsAnchor };
        Assert.Equal(0, falling.CheckAfterRemoval(8, 5, 15));
    }

    // ---- Die echte Test-Map ----

    [Fact]
    public void TestMap_HasSpawnZonesAndPointsForTwoFullTeams()
    {
        var registry = BlockRegistry.Parse(File.ReadAllText(TestData.RepoFile("data/blocks.json")));
        var map = MapFile.Load(TestData.RepoFile("maps/testmap.aos2map"), registry);
        var meta = map.Metadata;
        var zones = new MapZones(meta);
        Assert.NotNull(meta.CombatZone);
        foreach (var team in new[] { Team.Blue, Team.Green })
        {
            var spawn = meta.Teams[team];
            Assert.True(spawn.Points.Count >= 16, $"{team}: {spawn.Points.Count} Spawnpunkte");
            foreach (var p in spawn.Points)
            {
                Assert.Equal(team, zones.SpawnZoneAt(p));
                int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
                Assert.True(map.World.IsSolid(x, y - 1, z), $"Spawnpunkt {p} steht auf einem Block");
                Assert.False(PlayerMovement.Overlaps(map.World, p, Movement.HalfWidth, Movement.StandHeight), $"Spawnpunkt {p} ist frei");
            }
            // Spawnzone und Kampfzone überschneiden sich nicht: In der Kampfzone ist alles änderbar.
            var b = spawn.Zone;
            var c = meta.CombatZone!.Value;
            Assert.True(b.MaxZ <= c.MinZ || b.MinZ >= c.MaxZ || b.MaxX <= c.MinX || b.MinX >= c.MaxX, $"{team}: Spawn- und Kampfzone überschneiden sich");
            // Die Zonen reichen bis zum Himmel: Gegner kommen auch von oben nicht hinein.
            Assert.Equal(map.World.SizeY, b.MaxY);
        }
    }

    // ---- Protokoll ----

    [Fact]
    public void TeamEvents_RoundTrip()
    {
        var match = new MatchInfo
        {
            Phase = MatchPhase.Ended, ScoreBlue = 50, ScoreGreen = 47, ScoreLimit = 50, Winner = Team.Blue,
            PhaseEndTick = 123456, MatchNumber = 3, PlayersPerTeam = 16, FriendlyFire = true,
        };
        var w = new NetWriter(64);
        WorldEvents.WriteHeader(w);
        WorldEvents.WriteTeamChanged(w, 7, Team.Green);
        WorldEvents.WritePlayerStats(w, 7, 12, 70000);
        WorldEvents.WriteMatchState(w, match);
        WorldEvents.WriteTeamDenied(w, TeamDenyReason.Unbalanced);

        var r = new NetReader(w.ToArray());
        Assert.Equal((byte)MessageType.Events, r.ReadByte());
        var events = new List<WorldEvent>();
        WorldEvents.Read(r, events);
        Assert.Equal(4, events.Count);
        Assert.Equal((WorldEventType.TeamChanged, (byte)7, Team.Green), (events[0].Type, events[0].PlayerId, events[0].Team));
        Assert.Equal(((ushort)12, ushort.MaxValue), (events[1].Kills, events[1].Deaths));
        Assert.Equal(match, events[2].Match);
        Assert.Equal(47, events[2].Match.Score(Team.Green));
        Assert.Equal(TeamDenyReason.Unbalanced, events[3].DenyReason);
    }

    [Fact]
    public void TeamSelect_RoundTrip_RejectsUnknownTeam()
    {
        var w = new NetWriter(8);
        new TeamSelectRequest { Team = Team.Blue }.Write(w);
        var r = new NetReader(w.ToArray());
        Assert.Equal((byte)MessageType.TeamSelect, r.ReadByte());
        Assert.Equal(Team.Blue, TeamSelectRequest.Read(r).Team);

        Assert.Throws<ProtocolException>(() => TeamSelectRequest.Read(new NetReader([3])));
        Assert.Throws<ProtocolException>(() => WorldEvents.Read(new NetReader([(byte)WorldEventType.TeamDenied, 9]), new List<WorldEvent>()));
    }

    [Fact]
    public void Metadata_TeamNone_NeverWritten()
    {
        var json = Metadata().ToJson();
        Assert.DoesNotContain("none", json);
        var parsed = MapMetadata.Parse(json);
        Assert.Equal(2, parsed.Teams.Count);
        Assert.Equal(Team.Green, MapMetadata.Opponent(Team.Blue));
        Assert.Equal(Team.None, MapMetadata.Opponent(Team.None));
    }
}
