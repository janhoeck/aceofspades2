using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;

namespace AoS2.Server.Tests;

/// <summary>
/// Klassen im Netzspiel (Server und Clients in einem Prozess, simuliertes Netz): Wahl und Wechsel beim Respawn, Waffen und
/// Tempo je Klasse, Blockvorrat, Pause beim Graben, Ausrüstung als Trefferzone, Schadensabfall und die Regeln vom Server.
/// </summary>
public class ClassNetTests
{
    private static ServerPlayer Server(SimulatedNetwork net, TestClient c) => net.ServerPlayer(c);

    /// <summary>Tötet den Spieler (außerhalb der Spawnzone, dort gibt es keinen Schaden) und wartet, bis sein Client es weiß.</summary>
    private static void Kill(SimulatedNetwork net, TestClient c)
    {
        var p = Server(net, c);
        if (net.Match.Zones.InSpawnZone(p.State.Position)) net.Teleport(c, new Vec3(40.5f, 4, 40.5f));
        net.Match.Damage(p, p.Id, 1000, 0, 0);
        Assert.False(p.Alive);
        Assert.True(net.RunUntil(() => !c.Session.Alive, 1));
    }

    // ---- Wahl und Wechsel ----

    [Fact]
    public void NoChoice_SpawnsAsDefaultClass_ClientKnowsIt()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        Assert.True(net.RunUntil(() => a.Ready));
        net.Run(0.2);
        Assert.Same(TestArena.Assault, Server(net, a).Class);
        Assert.Equal("assault", a.Session.Class.Name);
        Assert.Same(a.Session.Class, a.Session.Prediction.Class);
        Assert.Equal(50, a.Session.BlocksLeft);
        Assert.Equal(30, a.Session.Prediction.Weapons.GetAmmo(1).Magazine);
        Assert.Equal(8, a.Session.Prediction.Weapons.GetAmmo(2).Magazine);
        Assert.Equal(3, a.Session.Prediction.Weapons.GetAmmo(3).Magazine);
    }

    [Fact]
    public void ChosenAsSpectator_SpawnsWithIt()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", team: null);
        Assert.True(net.RunUntil(() => a.Session.Phase == ClientPhase.Playing));
        Assert.Equal(0, a.Session.Class.Id); // Zuschauer: keine Klasse
        a.Session.RequestClass(a.Session.Classes.Find("recon"));
        a.Session.RequestTeam(Team.Blue);
        Assert.True(net.RunUntil(() => a.Ready));
        net.Run(0.2);

        Assert.Same(TestArena.Recon, Server(net, a).Class);
        Assert.Equal("recon", a.Session.Class.Name);
        Assert.Equal(TestArena.Recon.Movement.SprintSpeed, a.Session.Movement.SprintSpeed);
        Assert.Equal(5, a.Session.Prediction.Weapons.GetAmmo(1).Magazine); // Scharfschützengewehr
        Assert.Equal(30, a.Session.BlocksLeft);
    }

    [Fact]
    public void ChangeWhileAlive_KeepsClassUntilNextRespawn()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));

        a.Session.RequestClass(a.Session.Classes.Find("miner"));
        net.Run(0.5);
        Assert.Same(TestArena.Assault, Server(net, a).Class); // lebt: kein Wechsel, kein Auffüllen
        Assert.Same(TestArena.Miner, Server(net, a).NextClass);
        Assert.Equal("assault", a.Session.Class.Name);
        Assert.Equal("miner", a.Session.NextClass.Name);

        Kill(net, a);
        net.Run(1);
        Assert.Same(TestArena.Assault, Server(net, a).Class); // tot, aber noch nicht wieder da
        var mispredictions = a.Session.Prediction.Mispredictions;
        Assert.True(net.RunUntil(() => a.Ready, 6));
        net.Run(0.3);
        Assert.Same(TestArena.Miner, Server(net, a).Class);
        Assert.Equal("miner", a.Session.Class.Name);
        Assert.Equal(100, a.Session.BlocksLeft);
        Assert.Equal(32, a.Session.Prediction.Weapons.GetAmmo(1).Magazine); // MP
        Assert.Equal(TestArena.Miner.Id, b.Session.RemotePlayers[a.PlayerId].ClassId); // Ben sieht die neue Ausrüstung
        Assert.Equal(TestArena.Miner.Name, b.Session.ClassOf(a.PlayerId).Name);
        Assert.True(a.Session.Prediction.Mispredictions - mispredictions <= 1, "höchstens der Sprung zum Spawn");
    }

    [Fact]
    public void TeamChange_RespawnsWithChosenClass()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue);
        Assert.True(net.RunUntil(() => a.Ready));
        a.Session.RequestClass(a.Session.Classes.Find("rocketeer"));
        a.Session.RequestTeam(Team.Green);
        Assert.True(net.RunUntil(() => !a.Session.Alive, 1));
        Assert.True(net.RunUntil(() => a.Ready, 7));
        net.Run(0.2);
        Assert.Equal(Team.Green, a.Session.Team);
        Assert.Equal("rocketeer", a.Session.Class.Name);
        Assert.Equal(TestArena.Rocketeer.Movement.StandHeight, a.Session.Movement.StandHeight);
    }

    [Fact]
    public void UnknownClassId_IsProtocolError()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna");
        var w = new NetWriter();
        new ClassSelectRequest { Class = 7 }.Write(w); // gültiges Byte, aber der Server hat nur vier Klassen
        Assert.False(net.Match.HandleMessage(a.PlayerId, w.Buffer, 0, w.Length));
        w.Clear();
        new ClassSelectRequest { Class = 3 }.Write(w);
        Assert.True(net.Match.HandleMessage(a.PlayerId, w.Buffer, 0, w.Length));
        Assert.Same(TestArena.Recon, Server(net, a).NextClass);
    }

    // ---- Blockvorrat und Graben ----

    [Fact]
    public void BlockSupply_PlaceCosts_RemoveRefunds_EmptyRejected_RespawnRefills()
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", cls: TestArena.Recon); // 30 Blöcke
        Assert.True(net.RunUntil(() => a.Ready));
        net.Teleport(a, new Vec3(30.5f, 4, 30.5f));
        net.Run(0.3);
        Assert.Equal(30, a.Session.BlocksLeft);

        a.LookAt(new Vec3(30.5f, 4.5f, 32.5f));
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build));
        net.Frame();
        Assert.Equal(29, a.Session.BlocksLeft); // sofort, vor der Antwort des Servers
        net.Run(0.3);
        Assert.Equal(29, Server(net, a).Blocks);
        Assert.Equal(29, a.Session.BlocksLeft);

        // Abbauen gibt einen Block zurück, aber nie mehr als die Klasse trägt.
        a.AfterNextTick(c => c.RemoveTarget());
        net.Run(0.3);
        Assert.Equal(30, Server(net, a).Blocks);
        a.LookAt(new Vec3(30.5f, 3.5f, 32.5f));
        a.AfterNextTick(c => c.RemoveTarget());
        net.Run(0.3);
        Assert.Equal(BlockRegistry.Air, net.Match.World.GetBlock(30, 3, 32));
        Assert.Equal(30, Server(net, a).Blocks);
        Assert.Equal(30, a.Session.BlocksLeft);

        // Leer: Der Server lehnt ab und meldet den Vorrat, danach lehnt schon der Client ab.
        Server(net, a).Blocks = 0;
        a.LookAt(new Vec3(31.5f, 3.5f, 30.5f));
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build)); // der Client glaubt noch an 30 Blöcke
        net.Run(0.3);
        Assert.Equal([BlockEditResult.NoBlocks], a.Rejected);
        Assert.Equal(0, a.Session.BlocksLeft);
        a.AfterNextTick(c => c.PlaceAtTarget(TestArena.Build));
        net.Frame();
        Assert.Equal(BlockEditResult.NoBlocks, a.LocalResults[^1]);

        Kill(net, a);
        Assert.True(net.RunUntil(() => a.Ready, 7));
        net.Run(0.2);
        Assert.Equal(30, a.Session.BlocksLeft);
        AssertSameWorld(net.Match.World, a.Session.World);
    }

    /// <summary>Zwei Blöcke 8 Ticks nacheinander abbauen: Der Miner darf alle 6 Ticks, alle anderen alle 12.</summary>
    [Theory]
    [InlineData("miner", BlockEditResult.Ok)]
    [InlineData("assault", BlockEditResult.TooFast)]
    public void Digging_MinerIsFaster(string cls, BlockEditResult second)
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", cls: TestArena.Classes.Find(cls));
        Assert.True(net.RunUntil(() => a.Ready));
        net.Teleport(a, new Vec3(30.5f, 4, 30.5f));
        net.Run(0.3);
        a.LookAt(new Vec3(30.5f, 3.5f, 32.5f));
        a.AfterNextTick(c => c.RemoveTarget());
        net.Run(8 / 60.0);
        a.AfterNextTick(c => c.RemoveTarget());
        net.Run(0.5);
        Assert.Equal([BlockEditResult.Ok, BlockEditResult.Ok], a.LocalResults);
        Assert.Equal(second == BlockEditResult.Ok ? [] : [second], a.Rejected);
        AssertSameWorld(net.Match.World, a.Session.World);
    }

    // ---- Waffen ----

    private static (SimulatedNetwork Net, TestClient A, TestClient B) Duel(PlayerClass classA, PlayerClass classB, Vec3 posA, Vec3 posB)
    {
        var net = new SimulatedNetwork();
        var a = net.Join("Anna", Team.Blue, classA);
        var b = net.Join("Ben", Team.Green, classB);
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        net.Teleport(a, posA);
        net.Teleport(b, posB);
        net.Run(0.5);
        return (net, a, b);
    }

    [Theory]
    [InlineData(1.55f, HitZone.Head, 0)] // Kopfschuss tötet
    [InlineData(1.0f, HitZone.Torso, 30)] // 70 Schaden
    public void Sniper_HeadshotKills_TorsoTwoShots(float aimHeight, HitZone zone, int healthLeft)
    {
        var (net, a, b) = Duel(TestArena.Recon, TestArena.Assault, new Vec3(30.5f, 4, 5.5f), new Vec3(30.5f, 4, 55.5f));
        var reports = new List<ShotReport>();
        net.Match.ShotResolved += reports.Add;
        a.Input.Weapon = 1;
        net.Run(0.5);
        a.LookAt(net.ServerPlayer(b).State.Position + new Vec3(0, aimHeight, 0));
        a.FireTicks = 1;
        net.Run(0.5);

        var shot = Assert.Single(reports);
        Assert.Equal((b.PlayerId, zone), (shot.Victim, shot.Zone));
        Assert.Equal(healthLeft, Server(net, b).Health);
        var damaged = Assert.Single(b.Events, e => e.Type == WorldEventType.PlayerDamaged);
        Assert.Equal(TestArena.Combat.Find("sniper")!.Id, damaged.Weapon); // Waffen-ID in den Ereignissen
        Assert.Equal(4, a.Session.Prediction.Weapons.GetAmmo(1).Magazine);
    }

    [Fact]
    public void Rifle_LosesDamageOverDistance()
    {
        var (net, a, b) = Duel(TestArena.Assault, TestArena.Assault, new Vec3(30.5f, 4, 2.5f), new Vec3(30.5f, 4, 62.5f));
        var reports = new List<ShotReport>();
        net.Match.ShotResolved += reports.Add;
        a.Input.Weapon = 1;
        net.Run(0.5);
        a.LookAt(net.ServerPlayer(b).State.Position + new Vec3(0, 1.0f, 0));
        a.FireTicks = 1;
        net.Run(0.5);

        var shot = Assert.Single(reports);
        Assert.Equal(b.PlayerId, shot.Victim);
        var distance = Vec3.Distance(shot.From, shot.To);
        Assert.InRange(distance, 59f, 60f);
        var expected = (int)Math.Ceiling(TestArena.Combat.Find("rifle")!.DamageAt(distance) - 0.001f);
        Assert.Equal(22, expected); // 25 · (1 − 0,4 · 20/60) ≈ 21,7
        Assert.Equal(100 - expected, Server(net, b).Health);
    }

    /// <summary>
    /// Schuss von der Seite knapp hinter dem Rücken vorbei (0,3 Blöcke, Brusthöhe): Beim Rocketeer steckt dort der Rucksack
    /// (Trefferzone Rumpf), beim Assault ist dort nichts.
    /// </summary>
    [Theory]
    [InlineData("rocketeer", true)]
    [InlineData("assault", false)]
    public void Gear_BackpackIsHitZone_OnlyForRocketeer(string victimClass, bool hit)
    {
        var victim = new Vec3(30.5f, 4, 30.5f);
        var (net, a, b) = Duel(TestArena.Assault, TestArena.Classes.Find(victimClass)!, new Vec3(20.5f, 4, 30.2f), victim);
        var reports = new List<ShotReport>();
        net.Match.ShotResolved += reports.Add;
        a.Input.Weapon = 1;
        net.Run(0.5);
        a.LookAt(victim + new Vec3(0, 1.0f, -0.3f));
        a.FireTicks = 1;
        net.Run(0.5);

        var shot = Assert.Single(reports);
        if (hit)
        {
            Assert.Equal((b.PlayerId, HitZone.Torso), (shot.Victim, shot.Zone));
            Assert.Equal(75, Server(net, b).Health);
        }
        else
        {
            Assert.Null(shot.Victim);
            Assert.Equal(100, Server(net, b).Health);
        }
    }

    [Fact]
    public void Movement_ReconFaster_RocketeerSlower_PredictionExact()
    {
        var net = new SimulatedNetwork();
        var clients = new[] { TestArena.Assault, TestArena.Recon, TestArena.Rocketeer }
            .Select((cls, i) => net.Join($"Spieler {i}", cls: cls)).ToList();
        Assert.True(net.RunUntil(() => clients.All(c => c.Ready)));
        for (var i = 0; i < clients.Count; i++) net.Teleport(clients[i], new Vec3(26.5f + 4 * i, 4, 20.5f));
        net.Run(0.5);
        var start = clients.Select(c => Server(net, c).State.Position.Z).ToList();
        var mispredictions = clients.Select(c => c.Session.Prediction.Mispredictions).ToList(); // ohne den Sprung beim Teleport
        foreach (var c in clients) c.Input.Buttons = InputButtons.Forward | InputButtons.Sprint; // Blick +z
        net.Run(1.5);
        var moved = clients.Select((c, i) => Server(net, c).State.Position.Z - start[i]).ToList();

        Assert.Equal(1.1f, moved[1] / moved[0], 2);
        Assert.Equal(0.9f, moved[2] / moved[0], 2);
        Assert.Equal(mispredictions, clients.Select(c => c.Session.Prediction.Mispredictions));
    }

    // ---- Regeln vom Server ----

    private static GameContent ContentWith(Func<GameDataFiles, GameDataFiles> change)
    {
        var files = change(GameDataFiles.Load(name => File.ReadAllText(TestArena.RepoFile("data/" + name))));
        var rules = GameRules.Parse(files, TestArena.RigJson);
        var arena = TestArena.Content;
        return new GameContent(arena.Registry, rules, TestArena.RigJson, arena.MapName, arena.MapFileBytes);
    }

    /// <summary>Der Betreiber ändert Werte: Der Client rechnet mit denen des Servers, nicht mit seinen eigenen Dateien.</summary>
    [Fact]
    public void ServerRules_ReachClient_PredictionUsesThem()
    {
        var content = ContentWith(f =>
        {
            f.Weapons = f.Weapons.Replace("\"magazine\": 30, \"reserve\": 120", "\"magazine\": 10, \"reserve\": 20");
            f.Classes = f.Classes.Replace("\"blocks\": 50", "\"blocks\": 77");
            return f;
        });
        var net = new SimulatedNetwork(content: content);
        var a = net.Join("Anna");
        var b = net.Join("Ben");
        Assert.True(net.RunUntil(() => a.Ready && b.Ready));
        Assert.Equal(30, TestArena.Combat.Find("rifle")!.Magazine); // die eigenen Dateien des Clients
        Assert.True(a.Session.HasServerRules);
        Assert.Equal(10, a.Session.Combat.Find("rifle")!.Magazine);
        Assert.Equal(77, a.Session.Classes.Find("assault")!.Blocks);
        net.Run(0.2);
        Assert.Equal(77, a.Session.BlocksLeft);
        Assert.Equal(10, a.Session.Prediction.Weapons.GetAmmo(1).Magazine);

        a.Input.Weapon = 1;
        a.FireTicks = 30;
        net.Run(1.5);
        Assert.True(a.Session.Prediction.Weapons.Equals(Server(net, a).Weapons));
        Assert.Equal(0, a.Session.Prediction.Mispredictions);
    }

    [Fact]
    public void ServerRules_Broken_ClientStopsWithMessage()
    {
        var content = TestArena.Content;
        var session = new ClientSession(content.Registry, content.Rules, content.RigJson, _ => content.MapFileBytes, new NullSender(), () => 0);
        var files = GameDataFiles.Load(name => File.ReadAllText(TestArena.RepoFile("data/" + name)));
        files.Classes = """{ "classes": [] }""";
        var w = new NetWriter();
        files.Write(w);
        session.HandleMessage(w.Buffer, 0, w.Length);
        Assert.Equal(ClientPhase.Disconnected, session.Phase);
        Assert.Equal("Die Spielregeln des Servers sind ungültig: classes.json: \"classes\" ist leer.", session.Error);
    }

    // ---- Alle vier Klassen in einem Match ----

    /// <summary>
    /// Vier Spieler, jede Klasse einmal (2 gegen 2): Jeder spawnt mit seiner Ausrüstung, sieht die Klassen der anderen und
    /// kämpft mit seinen Waffen. Vorhersage von Bewegung und Waffen bleibt exakt, alle sehen dieselbe Welt.
    /// </summary>
    [Fact]
    public void FourClasses_OneMatch_EveryoneHasOwnLoadout()
    {
        var net = new SimulatedNetwork(seed: 3) { OneWayLatency = 0.04, Jitter = 0.02 };
        var clients = TestArena.Classes.All.Select((cls, i) => net.Join(cls.DisplayName, i % 2 == 0 ? Team.Blue : Team.Green, cls)).ToList();
        Assert.True(net.RunUntil(() => clients.All(c => c.Ready), 3));
        net.Run(0.3);

        foreach (var (c, cls) in clients.Zip(TestArena.Classes.All))
        {
            Assert.Same(cls, Server(net, c).Class);
            Assert.Equal(cls.Name, c.Session.Class.Name);
            Assert.Equal(cls.Blocks, c.Session.BlocksLeft);
            Assert.Equal(cls.Weapons.Select(w => (short)w.Magazine), Enumerable.Range(1, cls.SlotCount).Select(s => c.Session.Prediction.Weapons.GetAmmo(s).Magazine));
            foreach (var other in clients.Where(o => o != c))
                Assert.Equal(Server(net, other).Class.Id, c.Session.RemotePlayers[other.PlayerId].ClassId);
        }

        // Alle gehen aus dem Spawn und schießen mit ihrer ersten Waffe in die Luft (Rakete und Gewehre), der Miner gräbt.
        for (var i = 0; i < clients.Count; i++) net.Teleport(clients[i], new Vec3(24.5f + 5 * i, 4, 40.5f));
        foreach (var c in clients) c.Input.Weapon = 1; // erst wechseln: Wer beim Wechsel schon drückt, schießt mit Raketenwerfer und Gewehr ohne Dauerfeuer nicht
        net.Run(0.5);
        var mispredictions = clients.Select(c => c.Session.Prediction.Mispredictions).ToList(); // ohne den Sprung beim Teleport
        foreach (var c in clients)
        {
            c.Input.Pitch = -60;
            c.FireTicks = 40;
        }
        var miner = clients[0];
        miner.Input.Weapon = 0;
        miner.LookAt(new Vec3(24.5f, 3.5f, 42.5f));
        miner.AfterNextTick(c => c.RemoveTarget());
        net.Run(3);

        Assert.Equal(mispredictions, clients.Select(c => c.Session.Prediction.Mispredictions));
        Assert.All(clients, c => Assert.True(c.Session.Prediction.Weapons.Equals(Server(net, c).Weapons)));
        Assert.Contains(clients[3].Fired, slot => slot == 1); // Rakete
        Assert.Equal(TestArena.Classes.Find("assault")!.Blocks, Server(net, clients[1]).Blocks);
        Assert.Equal(TestArena.Miner.Blocks, Server(net, miner).Blocks); // voll: Abbauen gibt nichts über das Maximum
        Assert.Equal(BlockRegistry.Air, net.Match.World.GetBlock(24, 3, 42));
        foreach (var c in clients) AssertSameWorld(net.Match.World, c.Session.World);
    }

    private sealed class NullSender : IMessageSender
    {
        public void Send(NetWriter message, Delivery delivery)
        {
        }
    }

    private static void AssertSameWorld(VoxelWorld expected, VoxelWorld actual)
    {
        for (var y = 0; y < expected.SizeY; y++)
            for (var z = 0; z < expected.SizeZ; z++)
                for (var x = 0; x < expected.SizeX; x++)
                    if (expected.GetBlock(x, y, z) != actual.GetBlock(x, y, z))
                        Assert.Fail($"Welt weicht bei ({x}, {y}, {z}) ab: {expected.GetBlock(x, y, z)} statt {actual.GetBlock(x, y, z)}");
    }
}
