using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

/// <summary>Waffen (Feuerrate, Magazin, Nachladen, Wechsel), Geschossflug, Explosionen und Blockschaden.</summary>
public class CombatTests
{
    private static readonly CombatSettings Combat = CombatSettings.Parse(
        File.ReadAllText(TestData.RepoFile("data/combat.json")), File.ReadAllText(TestData.RepoFile("data/weapons.json")), 60);

    private static WeaponSettings Rifle => Combat.Weapons.Single(w => w.Name == "rifle");
    private static WeaponSettings Grenade => Combat.Weapons.Single(w => w.Name == "grenade");
    private static WeaponSettings Rocket => Combat.Weapons.Single(w => w.Name == "rocket");

    [Fact]
    public void Settings_RealData_ParsedInTicks()
    {
        Assert.Equal(100, Combat.Health);
        Assert.Equal(300, Combat.RespawnTicks);
        Assert.Equal(1f, Combat.SelfDamage);
        Assert.Equal(2f, Combat.ZoneFactor(HitZone.Head));
        Assert.Equal(0.67f, Combat.ZoneFactor(HitZone.Leg));
        Assert.Equal(3, Combat.SlotCount);
        Assert.Equal(1, Rifle.Slot);
        Assert.Equal(WeaponKind.Hitscan, Rifle.Kind);
        Assert.Equal(6, Rifle.FireIntervalTicks); // 10 Schuss/s
        Assert.True(Rifle.Automatic);
        Assert.Equal(120, Rifle.ReloadTicks);
        Assert.Equal(WeaponKind.Grenade, Grenade.Kind);
        Assert.Equal(180, Grenade.FuseTicks);
        Assert.Equal(WeaponKind.Rocket, Rocket.Kind);
        Assert.Equal(4f, Rocket.Explosion.Radius);
    }

    [Fact]
    public void Settings_BrokenData_NamesFileAndWeapon()
    {
        var e = Assert.Throws<FormatException>(() => CombatSettings.Parse("{}", """{ "weapons": [ { "name": "x", "kind": "laser", "fireInterval": 1, "magazine": 1 } ] }""", 60));
        Assert.Contains("weapons.json", e.Message);
        Assert.Contains("\"x\"", e.Message);
        Assert.Throws<FormatException>(() => CombatSettings.Parse("""{ "health": 0 }""", """{ "weapons": [] }""", 60));
        Assert.Throws<FormatException>(() => CombatSettings.Parse("{}", """{ "weapons": [ { "name": "g", "kind": "grenade", "fireInterval": 1, "magazine": 1 } ] }""", 60));
    }

    private static PlayerInput Hold(int slot, InputButtons buttons = InputButtons.Fire) => new() { Weapon = (byte)slot, Buttons = buttons };

    [Fact]
    public void Rifle_HoldFire_TenShotsPerSecond_ThenReloadsAutomatically()
    {
        var w = WeaponState.Full(Combat, 1);
        var shots = new List<int>();
        for (var tick = 0; tick < 60; tick++)
            if (WeaponSim.Step(ref w, Hold(1), Combat) == 1) shots.Add(tick);
        Assert.Equal(10, shots.Count);
        Assert.All(shots.Zip(shots.Skip(1)), p => Assert.Equal(6, p.Second - p.First));
        Assert.Equal(20, w.GetAmmo(1).Magazine);

        for (var tick = 0; tick < 120; tick++) WeaponSim.Step(ref w, Hold(1), Combat);
        Assert.Equal(0, w.GetAmmo(1).Magazine);
        Assert.True(w.Reload > 0, "leeres Magazin lädt von selbst nach");
        var before = w.GetAmmo(1).Reserve;
        for (var tick = 0; w.Reload > 0; tick++)
        {
            Assert.True(tick < Rifle.ReloadTicks);
            Assert.Equal(0, WeaponSim.Step(ref w, Hold(1), Combat));
        }
        Assert.Equal(30, w.GetAmmo(1).Magazine);
        Assert.Equal(before - 30, w.GetAmmo(1).Reserve);
    }

    [Fact]
    public void Rifle_ReloadButton_RefillsPartialMagazine()
    {
        var w = WeaponState.Full(Combat, 1);
        WeaponSim.Step(ref w, Hold(1), Combat);
        Assert.Equal(29, w.GetAmmo(1).Magazine);
        WeaponSim.Step(ref w, Hold(1, InputButtons.Reload), Combat);
        Assert.Equal(Rifle.ReloadTicks, w.Reload);
        for (var i = 0; i < Rifle.ReloadTicks; i++) WeaponSim.Step(ref w, Hold(1, 0), Combat);
        Assert.Equal(30, w.GetAmmo(1).Magazine);
        Assert.Equal(119, w.GetAmmo(1).Reserve);
    }

    [Fact]
    public void Grenade_OneThrowPerClick_ThreeInTotal()
    {
        var w = WeaponState.Full(Combat, 2);
        var thrown = 0;
        for (var tick = 0; tick < 200; tick++)
            if (WeaponSim.Step(ref w, Hold(2), Combat) != 0) thrown++;
        Assert.Equal(1, thrown); // gedrückt halten wirft nur einmal

        for (var click = 0; click < 10; click++)
        {
            for (var tick = 0; tick < 70; tick++)
                if (WeaponSim.Step(ref w, Hold(2, tick < 5 ? InputButtons.Fire : 0), Combat) != 0) thrown++;
        }
        Assert.Equal(3, thrown);
        Assert.Equal(0, w.GetAmmo(2).Magazine);
        Assert.Equal(0, w.Reload); // Granaten lädt man nicht nach
    }

    [Fact]
    public void Switch_WeaponNotReadyRightAway_ReloadCancelled()
    {
        var w = WeaponState.Full(Combat, 0);
        Assert.Equal(0, WeaponSim.Step(ref w, Hold(1), Combat)); // gerade gewechselt
        var ticks = 1;
        while (WeaponSim.Step(ref w, Hold(1), Combat) == 0) ticks++;
        Assert.Equal(Combat.SwitchTicks, ticks); // feuert im Tick SwitchTicks nach dem Wechsel

        WeaponSim.Step(ref w, Hold(1, InputButtons.Reload), Combat);
        Assert.True(w.Reload > 0);
        WeaponSim.Step(ref w, Hold(3, 0), Combat);
        Assert.Equal(0, w.Reload);
        Assert.Equal(3, w.Slot);
        Assert.Equal(0, WeaponSim.Step(ref w, Hold(0), Combat)); // Spaten feuert nie
    }

    [Fact]
    public void Prediction_WithWeapons_ReconcilesExactly()
    {
        var world = TestData.FlatWorld();
        var m = new MovementSettings();
        var start = new PlayerState { Position = new Vec3(16, 4, 16), OnGround = true };
        var client = new ClientPrediction(world, m, start, Combat);
        var server = new ClientPrediction(world, m, start, Combat);
        var fired = 0;
        for (var t = 0; t < 200; t++)
        {
            var input = new PlayerInput { Weapon = (byte)(t < 100 ? 1 : 3), Buttons = t % 3 == 0 ? InputButtons.Fire | InputButtons.Forward : InputButtons.Forward, Yaw = t };
            client.Step(input, out var slot);
            if (slot != 0) fired++;
            server.Step(input);
            if (t % 7 == 0) client.Reconcile(server.Tick, server.State, server.Weapons);
        }
        Assert.Equal(0, client.Mispredictions);
        Assert.True(fired > 5);
        Assert.True(client.Weapons.Equals(server.Weapons));

        // Server sieht weniger Munition (z. B. weil er einen Schuss anders gerechnet hat): wird übernommen und gezählt.
        var wrong = server.Weapons;
        wrong.SetAmmo(3, new WeaponAmmo(0, 0));
        client.Reconcile(server.Tick, server.State, wrong);
        Assert.Equal(1, client.Mispredictions);
        Assert.Equal(0, client.Weapons.GetAmmo(3).Magazine);
    }

    // ---- Geschosse ----

    [Fact]
    public void Rocket_FliesStraight_ExplodesInFrontOfWall()
    {
        var world = TestData.FlatWorld();
        for (var y = 4; y < 10; y++) world.SetBlock(20, y, 16, TestData.Stone);
        var p = new Projectile { Position = new Vec3(10.5f, 5.5f, 16.5f), Velocity = new Vec3(Rocket.Speed, 0, 0) };
        var ticks = 0;
        while (!ProjectileSim.Step(ref p, Rocket, world, 1 / 60f)) Assert.True(++ticks < 100);
        Assert.Equal(20f, p.Position.X, 0.05f);
        Assert.True(p.Position.X < 20, "vor der Wand, nicht darin");
        Assert.Equal(5.5f, p.Position.Y);
        Assert.Equal(16, ticks); // 9,5 Blöcke bei 35 Blöcken/s ≈ 16,3 Ticks: explodiert im 17. Tick
    }

    [Fact]
    public void Rocket_IntoTheSky_ExplodesAfterLifetime()
    {
        var p = new Projectile { Position = new Vec3(10, 5, 10), Velocity = new Vec3(0, Rocket.Speed, 0) };
        var ticks = 1;
        while (!ProjectileSim.Step(ref p, Rocket, TestData.FlatWorld(), 1 / 60f)) ticks++;
        Assert.Equal(Rocket.LifetimeTicks, ticks);
    }

    [Fact]
    public void Grenade_FallsBouncesRests_ExplodesAfterFuse()
    {
        var world = TestData.FlatWorld(); // Boden oben bei y = 4
        var p = new Projectile { Position = new Vec3(10, 6, 10), Velocity = new Vec3(8, 4, 0) };
        var minY = float.MaxValue;
        var bounced = false;
        var ticks = 1;
        while (!ProjectileSim.Step(ref p, Grenade, world, 1 / 60f))
        {
            minY = Math.Min(minY, p.Position.Y);
            if (p.Velocity.Y > 0 && p.Position.Y < 4.5f) bounced = true;
            ticks++;
        }
        Assert.Equal(Grenade.FuseTicks, ticks);
        Assert.True(bounced, "prallt vom Boden ab");
        Assert.True(minY >= 4, "fällt nicht in den Boden");
        Assert.Equal(4.02f, p.Position.Y, 0.02f);
        Assert.Equal(Vec3.Zero, p.Velocity);
        Assert.InRange(p.Position.X, 12, 20); // rollt etwas und bleibt liegen
    }

    [Fact]
    public void Grenade_BouncesOffWall()
    {
        var world = TestData.FlatWorld();
        for (var y = 4; y < 10; y++) world.SetBlock(12, y, 10, TestData.Stone);
        var p = new Projectile { Position = new Vec3(10.5f, 6, 10.5f), Velocity = new Vec3(10, 0, 0) };
        for (var i = 0; i < 30; i++) ProjectileSim.Step(ref p, Grenade, world, 1 / 60f);
        Assert.True(p.Position.X < 12, "nicht durch die Wand");
        Assert.True(p.Velocity.X <= 0, "zurückgeprallt");
    }

    // ---- Explosionen ----

    [Fact]
    public void Explosion_DestroysNearBlocks_DamagesFarther_SparesBedrock()
    {
        var world = TestData.FlatWorld(groundTop: 3); // Stein y 1 … 3, Grundgestein y 0
        var registry = BlockRegistry.Parse("""
            { "blocks": [
              { "id": 1, "name": "bedrock", "indestructible": true, "textures": { "all": "b" } },
              { "id": 2, "name": "stone", "health": 150, "textures": { "all": "s" } }
            ] }
            """);
        var damage = new BlockDamage(world, registry);
        var destroyed = new List<BlockPos>();
        var damaged = new List<BlockPos>();
        var center = new Vec3(16.5f, 4.02f, 16.5f);
        Explosions.DamageBlocks(world, damage, center, Rocket.Explosion.BlockRadius, Rocket.Explosion.BlockDamage, destroyed, damaged);

        Assert.Contains(destroyed, b => (b.X, b.Y, b.Z) == (16, 3, 16)); // direkt darunter
        Assert.DoesNotContain(destroyed, b => b.Y == 0);
        Assert.DoesNotContain(damaged, b => b.Y == 0);
        Assert.Contains(damaged, b => (b.X, b.Y, b.Z) == (18, 3, 16)); // 2 Blöcke daneben: nur beschädigt
        Assert.All(destroyed, b => Assert.True(Vec3.Distance(center, new Vec3(b.X + 0.5f, b.Y + 0.5f, b.Z + 0.5f)) < 1.2f));
        Assert.All(destroyed, b => Assert.True(world.IsSolid(b.X, b.Y, b.Z), "der Aufrufer entfernt sie"));
        Assert.True(damage.Level(18, 3, 16) is > 0 and < 255);

        // Zweite Explosion: der vorgeschädigte Block geht jetzt kaputt
        destroyed.Clear();
        Explosions.DamageBlocks(world, damage, center, Rocket.Explosion.BlockRadius, Rocket.Explosion.BlockDamage, destroyed, damaged);
        Assert.Contains(destroyed, b => (b.X, b.Y, b.Z) == (18, 3, 16) || (b.X, b.Y, b.Z) == (17, 3, 16));

        // Block weg: Schaden vergessen
        world.SetBlock(18, 3, 16, BlockRegistry.Air);
        Assert.Equal(0, damage.Get(18, 3, 16));
    }

    [Fact]
    public void Explosion_PlayerDamage_FallsOffWithDistance_BlockedByWalls()
    {
        var world = TestData.FlatWorld();
        var e = Rocket.Explosion;
        var center = new Vec3(10.5f, 4.1f, 10.5f);
        float At(float x) => Explosions.PlayerDamage(world, center, e.Radius, e.Damage, new Vec3(x, 4, 10.5f), 0.4f, 1.9f);

        Assert.Equal(e.Damage, At(10.5f), 0.01f); // mittendrin
        Assert.Equal(e.Damage * 0.5f, At(10.5f + 0.4f + e.Radius / 2), 0.5f);
        Assert.Equal(0, At(10.5f + 0.4f + e.Radius + 0.1f));
        Assert.True(At(12) > At(13));

        for (var y = 4; y < 8; y++)
            for (var z = 7; z < 14; z++) world.SetBlock(12, y, z, TestData.Stone);
        Assert.Equal(0, At(13.5f)); // hinter der Wand
    }

    [Fact]
    public void Explosion_UnderBridge_GroupFalls()
    {
        var world = TestData.FlatWorld();
        // Säule bei x = 10 mit Balken nach x = 11 … 14 in Höhe 8; Säule unten gesprengt → Balken und Rest fallen
        for (var y = 4; y <= 8; y++) world.SetBlock(10, y, 10, TestData.Stone);
        for (var x = 11; x <= 14; x++) world.SetBlock(x, 8, 10, TestData.Stone);
        var falling = new FallingBlocks(world);
        var removed = new List<BlockPos> { new(10, 4, 10), new(10, 5, 10) };
        foreach (var b in removed) world.SetBlock(b.X, b.Y, b.Z, BlockRegistry.Air);
        Assert.Equal(1, falling.CheckAfterRemovals(removed));
        Assert.Equal(3 + 4, falling.Groups[0].Blocks.Count);
    }

    // ---- Netz ----

    [Fact]
    public void Input_WithFire_CarriesViewTick()
    {
        var w = new NetWriter();
        NetCodec.WriteInput(w, new PlayerInput { Buttons = InputButtons.Fire | InputButtons.Forward, Yaw = 10, Pitch = -5, Weapon = 2, ViewTick = 12345.75 });
        NetCodec.WriteInput(w, new PlayerInput { Buttons = InputButtons.Reload, Weapon = 1, ViewTick = 99 });
        var r = new NetReader(w.ToArray());
        var a = NetCodec.ReadInput(r);
        var b = NetCodec.ReadInput(r);
        Assert.Equal(2, a.Weapon);
        Assert.Equal(12345.75, a.ViewTick, 1e-6);
        Assert.Equal(0, b.ViewTick); // ohne Feuern nicht übertragen
        Assert.True(r.AtEnd);
        Assert.Throws<ProtocolException>(() => NetCodec.ReadInput(new NetReader([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9])));
    }

    [Fact]
    public void Snapshot_CombatFields_RoundTrip()
    {
        var weapons = WeaponState.Full(Combat, 3);
        weapons.Reload = 77;
        weapons.Cooldown = 5;
        weapons.SetAmmo(1, new WeaponAmmo(12, 34));
        var anim = new AnimationState { Animation = 3, Start = 70_000, Previous = 1, PreviousStart = 69_000, BlendStart = 70_002 };
        var s = new Snapshot { ServerTick = 70_004, OwnWeapons = weapons, OwnHealth = 42, OwnRespawnTicks = 0, OwnLife = 3 };
        s.Players.Add(new PlayerSnapshot { Id = 1, Alive = true, Life = 9, Weapon = 2, Animation = anim });
        s.Players.Add(new PlayerSnapshot { Id = 2, Alive = false, Animation = new AnimationState { Animation = 4, Previous = 4, Start = 69_990 } });
        var w = new NetWriter();
        s.Write(w);
        var r = new NetReader(w.ToArray());
        r.ReadByte();
        var read = Snapshot.Read(r);

        Assert.True(read.OwnWeapons.Equals(weapons));
        Assert.Equal(42, read.OwnHealth);
        Assert.Equal(3, read.OwnLife);
        var p = read.Players[0];
        Assert.True(p.Alive);
        Assert.Equal((9, 2), (p.Life, p.Weapon));
        Assert.True(p.Animation.SameAs(anim));
        Assert.Equal(69_000u, p.Animation.PreviousStart);
        Assert.False(read.Players[1].Alive);
        Assert.Equal(69_990u, read.Players[1].Animation.Start);
        Assert.False(read.Players[1].Animation.IsBlending(70_004) && read.Players[1].Animation.Previous != 4);
    }

    [Fact]
    public void CombatEvents_RoundTrip()
    {
        var w = new NetWriter();
        WorldEvents.WriteHeader(w);
        WorldEvents.WriteBlockDamaged(w, 1, 2, 3, 200);
        WorldEvents.WritePlayerDamaged(w, 4, 5, 1, (byte)HitZone.Head, 50, 50);
        WorldEvents.WritePlayerKilled(w, 4, 5, 3, WorldEvent.ExplosionZone);
        WorldEvents.WriteShotFired(w, 5, 1, new Vec3(1, 2, 3), new Vec3(4, 5, 6), ShotHit.Block);
        WorldEvents.WriteProjectileSpawned(w, new Projectile { Id = 300, Weapon = 3, Owner = 5, Position = new Vec3(1, 1, 1), Velocity = new Vec3(0, 0, 35) }, 1234);
        WorldEvents.WriteProjectileExploded(w, 300, 3, new Vec3(7, 8, 9), 1300);
        var r = new NetReader(w.ToArray());
        r.ReadByte();
        var events = new List<WorldEvent>();
        WorldEvents.Read(r, events);

        Assert.Equal(200, events[0].Level);
        Assert.Equal((4, 5, 1, (byte)HitZone.Head, 50, 50), (events[1].PlayerId, events[1].Attacker, events[1].Weapon, events[1].Zone, events[1].Amount, events[1].Health));
        Assert.Equal((4, 5, 3, WorldEvent.ExplosionZone), (events[2].PlayerId, events[2].Attacker, events[2].Weapon, events[2].Zone));
        Assert.Equal((ShotHit.Block, new Vec3(4, 5, 6)), (events[3].Hit, events[3].Vector));
        Assert.Equal((300, 3, 5, 1234u, new Vec3(0, 0, 35)), (events[4].ProjectileId, events[4].Weapon, events[4].PlayerId, events[4].Tick, events[4].Vector));
        Assert.Equal((300, 1300u, new Vec3(7, 8, 9)), (events[5].ProjectileId, events[5].Tick, events[5].Position));
    }

    [Theory]
    [InlineData(70_000u, 70_004u)]
    [InlineData(65_530u, 65_540u)]
    [InlineData(0u, 3u)]
    public void Unwrap_RestoresFullTick(uint full, uint reference) => Assert.Equal(full, NetCodec.Unwrap((ushort)full, reference));

    [Fact]
    public void PlayerSnapshotLerp_NoInterpolationAcrossRespawn()
    {
        var dead = new PlayerSnapshot { Position = new Vec3(0, 0, 0), Alive = false, Life = 0 };
        var spawned = new PlayerSnapshot { Position = new Vec3(50, 0, 0), Alive = true, Life = 1 };
        Assert.Equal(spawned.Position, PlayerSnapshot.Lerp(dead, spawned, 0.3f).Position);
        var a = new PlayerSnapshot { Position = new Vec3(0, 0, 0), Yaw = 350, Alive = true };
        var b = new PlayerSnapshot { Position = new Vec3(2, 0, 0), Yaw = 10, Alive = true };
        var mid = PlayerSnapshot.Lerp(a, b, 0.5f);
        Assert.Equal(1, mid.Position.X);
        Assert.Equal(360, mid.Yaw, 0.001f);
    }
}
