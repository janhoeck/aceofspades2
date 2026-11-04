using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;

namespace AoS2.Shared.Tests;

/// <summary>Klassen (classes.json), neue Waffen und ihre Werte, Ausrüstung als Trefferzone, Protokoll der Klassen.</summary>
public class ClassesTests
{
    private static readonly GameRules Rules = TestData.RealRules();
    private static readonly PlayerRig Rig = PlayerRig.Parse(TestData.DataFile("player_rig.json"));

    private static PlayerClass Class(string name) => Rules.Classes.Find(name)!;

    // ---- Echte Daten ----

    [Fact]
    public void RealData_FourClassesWithOwnLoadout()
    {
        var classes = Rules.Classes;
        Assert.Equal(["miner", "assault", "recon", "rocketeer"], classes.All.Select(c => c.Name));
        Assert.Equal([1, 2, 3, 4], classes.All.Select(c => (int)c.Id));
        Assert.Same(Class("assault"), classes.Default);
        Assert.Equal(0, classes.None.Id);
        Assert.Empty(classes.None.Weapons);
        Assert.Same(classes.None, classes.Get(0));
        Assert.Null(classes.Get(5));

        Assert.Equal(["smg", "grenade"], Class("miner").Weapons.Select(w => w.Name));
        Assert.Equal(["rifle", "pistol", "grenade"], Class("assault").Weapons.Select(w => w.Name));
        Assert.Equal(["sniper", "pistol"], Class("recon").Weapons.Select(w => w.Name));
        Assert.Equal(["rocket", "pistol"], Class("rocketeer").Weapons.Select(w => w.Name));
        Assert.Equal([100, 50, 30, 30], classes.All.Select(c => c.Blocks));
        Assert.Equal([6, 12, 12, 12], classes.All.Select(c => c.EditCooldownTicks)); // Miner 0,1 s, sonst 0,2 s
        Assert.All(classes.All, c => Assert.Equal(100, c.Health));
        Assert.All(classes.All, c => Assert.Equal(c.Name, c.Gear));
        Assert.Equal(18, Class("recon").SwitchTicks);
    }

    [Fact]
    public void RealData_MovementPerClass_SpeedAndHeightWithGear()
    {
        var base_ = Rules.Movement;
        Assert.Equal(base_.WalkSpeed, Class("assault").Movement.WalkSpeed);
        Assert.Equal(base_.SprintSpeed * 1.1f, Class("recon").Movement.SprintSpeed, 4);
        Assert.Equal(base_.WalkSpeed * 0.9f, Class("rocketeer").Movement.WalkSpeed, 4);
        Assert.Equal(base_.CrouchSpeed * 1.1f, Class("recon").Movement.CrouchSpeed, 4);

        // Kollisionshöhe aus stances: ohne Ausrüstung 1,902, mit Helm 1,968, mit Raketenhelm 1,982.
        Assert.Equal(1.902f, base_.StandHeight, 3);
        Assert.Equal(1.968f, Class("assault").Movement.StandHeight, 3);
        Assert.Equal(1.982f, Class("rocketeer").Movement.StandHeight, 3);
        Assert.Equal(1.542f, Class("miner").Movement.CrouchHeight, 3);
        Assert.Same(base_.GetType(), Rules.Classes.None.Movement.GetType());
        Assert.Equal(base_.StandHeight, Rules.Classes.None.Movement.StandHeight);
    }

    [Fact]
    public void RealData_NewWeapons_Values()
    {
        var combat = Rules.Combat;
        Assert.Equal(["rifle", "grenade", "rocket", "smg", "sniper", "pistol"], combat.Weapons.Select(w => w.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], combat.Weapons.Select(w => w.Id)); // IDs bleiben, neue Waffen kommen hinten dazu
        Assert.Equal(1f, combat.MinHitboxPixels);

        var smg = combat.Find("smg")!;
        Assert.Equal(4, smg.FireIntervalTicks); // 15 Schuss/s
        Assert.True(smg.Automatic);
        Assert.Equal("mp5", smg.Model);

        var sniper = combat.Find("sniper")!;
        Assert.Equal(72, sniper.FireIntervalTicks);
        Assert.False(sniper.Automatic);
        Assert.Equal(4f, sniper.Zoom);
        Assert.False(sniper.HasFalloff);
        Assert.True(sniper.Damage * combat.ZoneFactor(HitZone.Head) >= combat.Health, "Kopfschuss tötet");
        Assert.True(sniper.Damage * 2 >= combat.Health, "zwei Rumpftreffer töten");

        var pistol = combat.Find("pistol")!;
        Assert.Equal(12, pistol.FireIntervalTicks);
        Assert.Equal(8, pistol.Magazine);
        Assert.Equal(1f, combat.Find("rifle")!.Zoom);
    }

    [Fact]
    public void Falloff_FullUntilStart_ThenLinear_ThenMinimum()
    {
        var rifle = Rules.Combat.Find("rifle")!; // 25, voll bis 40, 60 % ab 100
        Assert.Equal(25f, rifle.DamageAt(0));
        Assert.Equal(25f, rifle.DamageAt(40));
        Assert.Equal(20f, rifle.DamageAt(70), 3);
        Assert.Equal(15f, rifle.DamageAt(100), 3);
        Assert.Equal(15f, rifle.DamageAt(180), 3);
        var sniper = Rules.Combat.Find("sniper")!;
        Assert.Equal(sniper.Damage, sniper.DamageAt(299));
    }

    // ---- Ungültige Daten: klare Meldung mit Datei, Eintrag und Grund ----

    private static string Classes(string classes, string extra = "") => $$"""{ {{extra}} "classes": [ {{classes}} ] }""";

    private const string Miner = """{ "name": "miner", "gear": "miner", "weapons": ["smg"], "blocks": 10, "editSeconds": 0.1 }""";

    private static FormatException ParseClasses(string json) =>
        Assert.Throws<FormatException>(() => PlayerClasses.Parse(json, Rules.Combat, TestData.DataFile("movement.json"), TestData.DataFile("player_rig.json")));

    public static TheoryData<string, string> BrokenClasses => new()
    {
        { Classes(""), "ist leer" },
        { """{ "classes": 3 }""", "muss ein Array sein" },
        { Classes("""{ "weapons": [], "blocks": 1, "editSeconds": 1 }"""), "Nr. 1 hat keinen \"name\"" },
        { Classes(Miner + "," + Miner), "\"miner\" steht doppelt" },
        { Classes("""{ "name": "x", "weapons": ["laser"], "blocks": 1, "editSeconds": 1 }"""), "Klasse \"x\": Die Waffe \"laser\" gibt es in weapons.json nicht" },
        { Classes("""{ "name": "x", "weapons": ["smg", "smg"], "blocks": 1, "editSeconds": 1 }"""), "\"smg\" steht doppelt" },
        { Classes("""{ "name": "x", "weapons": ["smg", "rifle", "pistol", "sniper", "rocket"], "blocks": 1, "editSeconds": 1 }"""), "höchstens 4 Waffen" },
        { Classes("""{ "name": "x", "gear": "pirate", "weapons": [], "blocks": 1, "editSeconds": 1 }"""), "Ausrüstung \"pirate\" gibt es in player_rig.json nicht" },
        { Classes("""{ "name": "x", "weapons": [], "blocks": -1, "editSeconds": 1 }"""), "\"blocks\" muss zwischen 0 und 999" },
        { Classes("""{ "name": "x", "weapons": [], "editSeconds": 1 }"""), "\"blocks\"" },
        { Classes("""{ "name": "x", "weapons": [], "blocks": 1 }"""), "\"editSeconds\"" },
        { Classes("""{ "name": "x", "weapons": [], "blocks": 1, "editSeconds": 0 }"""), "\"editSeconds\" muss zwischen" },
        { Classes("""{ "name": "x", "weapons": [], "blocks": 1, "editSeconds": 1, "speed": 3 }"""), "\"speed\" muss zwischen 0,5 und 2" },
        { Classes("""{ "name": "x", "weapons": [], "blocks": 1, "editSeconds": 1, "health": 0 }"""), "\"health\" muss zwischen 1 und 255" },
        { Classes(Miner, "\"defaultClass\": \"ninja\","), "\"defaultClass\" \"ninja\" ist keine der Klassen" },
        { Classes("""{ "name": "none", "weapons": [], "blocks": 1, "editSeconds": 1 }"""), "\"none\" ist kein erlaubter Klassenname" },
    };

    [Theory]
    [MemberData(nameof(BrokenClasses))]
    public void Classes_BrokenData_RejectedWithClearMessage(string json, string expected)
    {
        var e = ParseClasses(json);
        Assert.StartsWith("classes.json", e.Message);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Classes_TooMany_Rejected()
    {
        var list = string.Join(",", Enumerable.Range(1, 8).Select(i => $$"""{ "name": "c{{i}}", "weapons": [], "blocks": 1, "editSeconds": 1 }"""));
        Assert.Contains("höchstens 7 Klassen", ParseClasses(Classes(list)).Message);
    }

    private const string Rifle = """{ "name": "rifle", "kind": "hitscan", "damage": 25, "fireInterval": 0.1, "magazine": 30 }""";

    public static TheoryData<string, string> BrokenWeapons => new()
    {
        { $$"""{ "weapons": [ {{Rifle}}, {{Rifle}} ] }""", "\"rifle\" gibt es doppelt" },
        { """{ "weapons": [ { "kind": "hitscan" } ] }""", "Waffe Nr. 1 hat keinen \"name\"" },
        { """{ "weapons": [] }""", "\"weapons\" ist leer" },
        { """{ "weapons": [ { "name": "r", "kind": "hitscan", "fireInterval": 0.1, "magazine": 30, "falloff": { "start": 50, "end": 20, "minFactor": 0.5 } } ] }""", "Waffe \"r\": \"falloff\": es muss 0 ≤ start < end gelten" },
        { """{ "weapons": [ { "name": "r", "kind": "hitscan", "fireInterval": 0.1, "magazine": 30, "falloff": { "start": 1, "end": 20, "minFactor": 2 } } ] }""", "\"falloff.minFactor\" muss zwischen 0 und 1" },
        { """{ "weapons": [ { "name": "r", "kind": "hitscan", "fireInterval": 0.1, "magazine": 30, "falloff": { "start": 1, "minFactor": 0.5 } } ] }""", "\"end\"" },
        { """{ "weapons": [ { "name": "g", "kind": "grenade", "fireInterval": 1, "magazine": 1, "speed": 1, "explosion": { "radius": 1, "damage": 1 }, "falloff": { "start": 1, "end": 2, "minFactor": 0.5 } } ] }""", "\"falloff\" gibt es nur für hitscan" },
        { """{ "weapons": [ { "name": "s", "kind": "hitscan", "fireInterval": 1, "magazine": 5, "zoom": 0.5 } ] }""", "\"zoom\" muss zwischen 1 und 16" },
        { """{ "weapons": [ { "name": "s", "kind": "hitscan", "fireInterval": 1, "magazine": 5, "reloadSeconds": -1 } ] }""", "\"reloadSeconds\" zwischen 0" },
    };

    [Theory]
    [MemberData(nameof(BrokenWeapons))]
    public void Weapons_BrokenData_RejectedWithClearMessage(string json, string expected)
    {
        var e = Assert.Throws<FormatException>(() => CombatSettings.Parse("{}", json, 60));
        Assert.StartsWith("weapons.json", e.Message);
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Combat_NegativeMinHitbox_Rejected()
    {
        var e = Assert.Throws<FormatException>(() => CombatSettings.Parse("""{ "minHitboxPixels": -1 }""", TestData.DataFile("weapons.json"), 60));
        Assert.Contains("minHitboxPixels", e.Message);
    }

    [Fact]
    public void GameRules_BrokenClassFile_NamesTheFile()
    {
        var files = GameDataFiles.Load(TestData.DataFile);
        files.Classes = files.Classes.Replace("\"sniper\"", "\"railgun\"");
        var e = Assert.Throws<FormatException>(() => GameRules.Parse(files, TestData.DataFile("player_rig.json")));
        Assert.Equal("classes.json, Klasse \"recon\": Die Waffe \"railgun\" gibt es in weapons.json nicht.", e.Message);
    }

    // ---- Waffen je Klasse ----

    [Fact]
    public void Loadout_SlotsFollowClass_UnknownSlotIsSpade()
    {
        var recon = Class("recon");
        var w = WeaponState.Full(recon, 1);
        Assert.Equal((short)5, w.GetAmmo(1).Magazine);
        Assert.Equal((short)8, w.GetAmmo(2).Magazine);
        Assert.Equal((short)0, w.GetAmmo(3).Magazine); // Recon hat keinen dritten Platz
        WeaponSim.Step(ref w, new PlayerInput { Weapon = 3 }, recon);
        Assert.Equal(0, w.Slot);
        Assert.Equal(5, recon.WeaponId(1));
        Assert.Equal(0, recon.WeaponId(3));
    }

    [Fact]
    public void Sniper_OneShotPerClick_FiveInMagazine_ThenReload()
    {
        var recon = Class("recon");
        var w = WeaponState.Full(recon, 1);
        var shots = new List<int>();
        for (var tick = 0; tick < 600; tick++)
            if (WeaponSim.Step(ref w, new PlayerInput { Weapon = 1, Buttons = tick % 80 < 2 ? InputButtons.Fire : 0 }, recon) == 1) shots.Add(tick);
        Assert.True(shots.Count >= 5);
        Assert.All(shots.Zip(shots.Skip(1)), p => Assert.True(p.Second - p.First >= 72, "höchstens ein Schuss alle 1,2 s"));
        // Gedrückt halten schießt nur einmal.
        w = WeaponState.Full(recon, 1);
        var held = 0;
        for (var tick = 0; tick < 300; tick++)
            if (WeaponSim.Step(ref w, new PlayerInput { Weapon = 1, Buttons = InputButtons.Fire }, recon) != 0) held++;
        Assert.Equal(1, held);
    }

    [Fact]
    public void Smg_HoldFire_FifteenShotsPerSecond()
    {
        var miner = Class("miner");
        var w = WeaponState.Full(miner, 1);
        var shots = 0;
        for (var tick = 0; tick < 60; tick++)
            if (WeaponSim.Step(ref w, new PlayerInput { Weapon = 1, Buttons = InputButtons.Fire }, miner) == 1) shots++;
        Assert.Equal(15, shots);
        Assert.Equal((short)17, w.GetAmmo(1).Magazine);
    }

    // ---- Ausrüstung als Trefferzone ----

    /// <summary>Strahl senkrecht von oben auf die Mitte des Kopfes: Mit Kopfbedeckung trifft er sie, und zwar früher.</summary>
    [Theory]
    [InlineData("miner", "miner_helmet_shell")]
    [InlineData("assault", "assault_helmet_shell")]
    [InlineData("recon", "recon_hat_crown")]
    [InlineData("rocketeer", "rocketeer_helmet_shell")]
    public void Gear_HeadwearIsHeadZone_OnlyForItsClass(string cls, string boxName)
    {
        var pose = new PlayerPose(Rig, Rules.Combat.MinHitboxPixels);
        pose.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        var feet = new Vec3(10, 4, 10);
        var from = new Vec3(10, 8, 10);
        var down = new Vec3(0, -1, 0);

        Assert.True(pose.Raycast(feet, 0, null, from, down, 10, out var bare, out var box));
        Assert.Null(Rig.Boxes[box].Gear);
        Assert.Equal(HitZone.Head, Rig.Boxes[box].Zone);

        Assert.True(pose.Raycast(feet, 0, cls, from, down, 10, out var withGear, out box));
        Assert.Equal(boxName, Rig.Boxes[box].Name);
        Assert.Equal(HitZone.Head, Rig.Boxes[box].Zone);
        Assert.True(withGear < bare - 0.05f, $"{cls}: Helm/Hut liegt über dem Kopf ({withGear} gegen {bare})");

        // Die Ausrüstung anderer Klassen zählt nicht.
        foreach (var other in Rules.Classes.All.Where(c => c.Name != cls))
        {
            Assert.True(pose.Raycast(feet, 0, other.Gear, from, down, 10, out _, out box));
            Assert.NotEqual(boxName, Rig.Boxes[box].Name);
        }
    }

    [Fact]
    public void Gear_RocketeerBackpack_IsTorso_OthersShootPast()
    {
        var pose = new PlayerPose(Rig, Rules.Combat.MinHitboxPixels);
        pose.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        var feet = new Vec3(10, 4, 10);
        // Spieler blickt nach +z, der Rucksack hängt hinten (−z). Strahl von der Seite, 0,3 Blöcke hinter dem Rücken, Brusthöhe.
        var from = new Vec3(5, 5, 10 - 0.3f);
        var dir = new Vec3(1, 0, 0);
        Assert.True(pose.Raycast(feet, 0, "rocketeer", from, dir, 10, out _, out var box));
        Assert.Equal("rocketeer", Rig.Boxes[box].Gear);
        Assert.Equal(HitZone.Torso, Rig.Boxes[box].Zone);
        Assert.False(pose.Raycast(feet, 0, "assault", from, dir, 10, out _, out _));
        Assert.False(pose.Raycast(feet, 0, null, from, dir, 10, out _, out _));
    }

    [Fact]
    public void Gear_ThinAntenna_IsOnlyVisual()
    {
        var antenna = Rig.Boxes.Single(b => b.Name == "recon_radio_antenna");
        var antennaIndex = Rig.Boxes.ToList().IndexOf(antenna);
        var strict = new PlayerPose(Rig, Rules.Combat.MinHitboxPixels);
        var all = new PlayerPose(Rig);
        Assert.False(strict.IsHittable(antennaIndex));
        Assert.False(strict.IsHittable(Rig.Boxes.ToList().FindIndex(b => b.Name == "recon_radio_knob"))); // 0,6 px
        Assert.True(all.IsHittable(antennaIndex));
        Assert.Equal(["recon_radio_antenna", "recon_radio_knob"], Rig.Boxes.Where((b, i) => !strict.IsHittable(i)).Select(b => b.Name));

        // Von der Seite auf die Antenne, zwischen Funkgerät und Hutrand (23 px hoch, 4,5 px hinter dem Kopf): Ohne Grenze trifft
        // der Strahl die Antenne, mit Grenze geht er vorbei.
        strict.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        all.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        var feet = new Vec3(10, 4, 10);
        var center = (antenna.From + antenna.To) * 0.5f;
        var from = new Vec3(5, 4 + 23f / 16, 10 - center.Z / 16); // Modell → Welt: z gespiegelt (Blick nach +z)
        Assert.True(all.Raycast(feet, 0, "recon", from, new Vec3(1, 0, 0), 10, out _, out var box));
        Assert.Equal("recon_radio_antenna", Rig.Boxes[box].Name);
        Assert.False(strict.Raycast(feet, 0, "recon", from, new Vec3(1, 0, 0), 10, out _, out _));
    }

    // ---- Protokoll ----

    [Fact]
    public void Snapshot_ClassesRoundTrip_InFlagBits()
    {
        var s = new Snapshot { ServerTick = 500, OwnClass = 3 };
        for (byte c = 0; c <= 7; c++) s.Players.Add(new PlayerSnapshot { Id = c, Class = c, Alive = c % 2 == 0, Crouching = true, Animation = new AnimationState { Start = 490 } });
        var w = new NetWriter(2048);
        s.Write(w);
        var r = new NetReader(w.Buffer, 0, w.Length);
        Assert.Equal(MessageType.Snapshot, (MessageType)r.ReadByte());
        var read = Snapshot.Read(r);
        Assert.Equal(3, read.OwnClass);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (byte)i), read.Players.Select(p => p.Class));
        Assert.All(read.Players, p => Assert.True(p.Crouching));
        Assert.Equal([true, false, true, false, true, false, true, false], read.Players.Select(p => p.Alive));
        // Ein Spieler bleibt höchstens MaxPlayerBytes groß.
        var one = new Snapshot { ServerTick = 500 };
        one.Players.Add(new PlayerSnapshot { Class = 7, Animation = new AnimationState { Start = 490, Previous = 1, BlendStart = 499 } });
        var w1 = new NetWriter();
        one.Write(w1);
        Assert.True(w1.Length <= Snapshot.HeaderBytes + Snapshot.MaxPlayerBytes);
    }

    [Fact]
    public void Snapshot_InvalidOwnClass_Throws()
    {
        var w = new NetWriter();
        new Snapshot { OwnClass = 8 }.Write(w);
        var r = new NetReader(w.Buffer, 1, w.Length - 1);
        Assert.Throws<ProtocolException>(() => Snapshot.Read(r));
    }

    [Fact]
    public void ClassSelect_RoundTrip_ZeroAndOutOfRangeRejected()
    {
        var w = new NetWriter();
        new ClassSelectRequest { Class = 4 }.Write(w);
        var r = new NetReader(w.ToArray());
        Assert.Equal(MessageType.ClassSelect, (MessageType)r.ReadByte());
        Assert.Equal(4, ClassSelectRequest.Read(r).Class);
        Assert.Throws<ProtocolException>(() => ClassSelectRequest.Read(new NetReader([0])));
        Assert.Throws<ProtocolException>(() => ClassSelectRequest.Read(new NetReader([8])));
        Assert.Throws<ProtocolException>(() => ClassSelectRequest.Read(new NetReader([1, 0])));
    }

    [Fact]
    public void GameData_RoundTrip_ParsesToSameRules()
    {
        var files = GameDataFiles.Load(TestData.DataFile);
        var w = new NetWriter();
        files.Write(w);
        var r = new NetReader(w.ToArray());
        Assert.Equal(MessageType.GameData, (MessageType)r.ReadByte());
        var read = GameDataFiles.Read(r);
        Assert.Equal(files.Classes, read.Classes);
        Assert.Equal(files.Weapons, read.Weapons);
        var rules = GameRules.Parse(read, TestData.DataFile("player_rig.json"));
        Assert.Equal(Rules.Classes.All.Select(c => c.Name), rules.Classes.All.Select(c => c.Name));
        Assert.True(w.Length < 4 * GameDataFiles.MaxFileBytes);
    }

    [Fact]
    public void BalanceMath_HitsAndTimeToKill()
    {
        var combat = Rules.Combat;
        var rifle = combat.Find("rifle")!;
        Assert.Equal(4, BalanceMath.HitsToKill(rifle, HitZone.Torso, 0, combat, 100));
        Assert.Equal(2, BalanceMath.HitsToKill(rifle, HitZone.Head, 0, combat, 100));
        Assert.Equal(6, BalanceMath.HitsToKill(rifle, HitZone.Leg, 0, combat, 100)); // 16,75 → 17 je Treffer
        Assert.Equal(7, BalanceMath.HitsToKill(rifle, HitZone.Torso, 100, combat, 100)); // 15 je Treffer
        Assert.Equal(0.3, BalanceMath.TimeToKill(rifle, 4, combat.TickRate), 6); // drei Pausen à 0,1 s
        var sniper = combat.Find("sniper")!;
        Assert.Equal(1, BalanceMath.HitsToKill(sniper, HitZone.Head, 250, combat, 100));
        Assert.Equal(0, BalanceMath.TimeToKill(sniper, 1, combat.TickRate));
        var pistol = combat.Find("pistol")!;
        // 13 Treffer brauchen ein Nachladen (Magazin 8): 11 Pausen à 0,2 s, dazu 1,5 s Nachladen und ein Tick.
        Assert.Equal(11 * 0.2 + 1.5 + 1 / 60.0, BalanceMath.TimeToKill(pistol, 13, combat.TickRate), 6);
        Assert.Equal(double.PositiveInfinity, BalanceMath.TimeToKill(pistol, 41, combat.TickRate)); // nur 8 + 32 Schuss
        Assert.Equal(int.MaxValue, BalanceMath.HitsToKill(new WeaponSettings { Damage = 0 }, HitZone.Torso, 0, combat, 100));
    }
}
