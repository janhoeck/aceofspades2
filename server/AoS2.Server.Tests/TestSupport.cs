using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AoS2.Server.Tests;

/// <summary>
/// Kleine Test-Arena (64 × 32 × 64): Grundgestein auf y = 0, Stein bis y = 3 (man steht auf y = 4).
/// Spawn A bei (16,5 | 4 | 16,5), Spawn B bei (19,5 | 4 | 16,5).
/// Westlich davon eine Säule (13, 4…6, 16) mit Verbindungsblock (13, 7, 16) und einem Balken (8…12, 7, 16):
/// Wird der Verbindungsblock abgebaut, fällt der Balken drei Blöcke tief.
/// </summary>
internal static class TestArena
{
    public const byte Bedrock = 1;
    public const byte Stone = 2;
    public const byte Dirt = 3;
    public const byte Build = 9;

    public const string MapName = "testarena";

    public static readonly Vec3 SpawnA = new(16.5f, 4, 16.5f);
    public static readonly Vec3 SpawnB = new(19.5f, 4, 16.5f);

    /// <summary>Spawnzone Blau: die Säule über (16, 4, 16) bis y = 8, darin Spawn A.</summary>
    public static readonly BlockBox BlueZone = new(16, 4, 16, 17, 8, 17);

    /// <summary>Spawnzone Grün: die Säule über (19, 4, 16) bis y = 8, darin Spawn B.</summary>
    public static readonly BlockBox GreenZone = new(19, 4, 16, 20, 8, 17);

    public static BlockRegistry Registry { get; } = BlockRegistry.Parse("""
        { "blocks": [
          { "id": 1, "name": "bedrock", "indestructible": true, "textures": { "all": "b" } },
          { "id": 2, "name": "stone", "textures": { "all": "s" } },
          { "id": 3, "name": "dirt", "textures": { "all": "d" } },
          { "id": 9, "name": "build_blue", "buildable": true, "textures": { "all": "x" } }
        ] }
        """);

    public static MovementSettings Movement { get; } = new();

    /// <summary>Kampfregeln, Waffen und Spielermodell aus dem echten data/-Ordner.</summary>
    public static CombatSettings Combat { get; } = CombatSettings.Parse(File.ReadAllText(RepoFile("data/combat.json")), File.ReadAllText(RepoFile("data/weapons.json")), 60);

    public static PlayerRig Rig { get; } = PlayerRig.Parse(File.ReadAllText(RepoFile("data/player_rig.json")));

    public static GameContent Content { get; } = CreateContent();

    /// <summary>Pfad einer Datei relativ zum Repository (sucht AoS2.slnx von der Testausgabe aufwärts).</summary>
    public static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AoS2.slnx"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Repository-Wurzel (AoS2.slnx) nicht gefunden.");
        return Path.Combine(dir.FullName, relativePath);
    }

    private static GameContent CreateContent()
    {
        var world = new VoxelWorld(64, 32, 64);
        for (var x = 0; x < 64; x++)
            for (var z = 0; z < 64; z++)
                for (var y = 0; y <= 3; y++)
                    world.SetBlock(x, y, z, y == 0 ? Bedrock : Stone);
        for (var y = 4; y <= 7; y++) world.SetBlock(13, y, 16, Stone);
        for (var x = 8; x <= 12; x++) world.SetBlock(x, 7, 16, Build);

        // Winzige Spawnzonen (eine Blocksäule um den Spawnpunkt), damit sie die übrigen Tests nicht stören.
        var metadata = new MapMetadata { Name = "Test-Arena" };
        metadata.Teams[Team.Blue] = new TeamSpawn { Zone = BlueZone, Points = { SpawnA } };
        metadata.Teams[Team.Green] = new TeamSpawn { Zone = GreenZone, Points = { SpawnB } };

        using var file = new MemoryStream();
        MapFile.Write(file, world, metadata, Registry);
        return new GameContent(Registry, Movement, Combat, Rig, MapName, file.ToArray());
    }

    /// <summary>Blickwinkel vom Auge eines stehenden Spielers bei <paramref name="feet"/> auf einen Punkt.</summary>
    public static (float Yaw, float Pitch) Aim(Vec3 feet, Vec3 target)
    {
        var eye = feet + new Vec3(0, new MovementSettings().EyeHeight(false), 0);
        double dx = target.X - eye.X, dy = target.Y - eye.Y, dz = target.Z - eye.Z;
        var yaw = Math.Atan2(dx, dz) * 180 / Math.PI;
        if (yaw < 0) yaw += 360;
        var pitch = -Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI;
        return ((float)yaw, (float)pitch);
    }
}

/// <summary>
/// Ein simuliertes Netz zwischen einem <see cref="Match"/> und beliebig vielen <see cref="ClientSession"/>s, mit
/// einstellbarer Verzögerung, Schwankung (Jitter) und Paketverlust. Alles läuft in einem Thread und mit simulierter
/// Zeit: Ein <see cref="Frame"/> ist 1/60 s, in dem jeder Client einen Tick rechnet und der Server einen Tick.
/// Zuverlässige Nachrichten gehen nie verloren, verspäten sich bei „Verlust“ aber um eine Neusendung.
/// </summary>
internal sealed class SimulatedNetwork : IMatchOutput
{
    public const double FrameSeconds = 1.0 / 60;

    private readonly Dictionary<byte, TestClient> _byPlayer = new();
    private readonly List<TestClient> _clients = new();

    public SimulatedNetwork(int seed = 1, GameContent? content = null, MatchSettings? settings = null)
    {
        Random = new Random(seed);
        Content = content ?? TestArena.Content;
        Match = new Match(Content, this, NullLogger.Instance, settings);
    }

    public GameContent Content { get; }
    public Match Match { get; }
    public Random Random { get; }
    public double Time { get; private set; }

    /// <summary>Verzögerung in eine Richtung (Ping ist das Doppelte).</summary>
    public double OneWayLatency { get; set; } = 0.05;

    /// <summary>Zusätzliche zufällige Verzögerung 0 … Jitter.</summary>
    public double Jitter { get; set; }

    /// <summary>Anteil verlorener Pakete (0 … 1).</summary>
    public double Loss { get; set; }

    /// <summary>
    /// Ein Client tritt bei und wählt gleich ein Team (<see cref="Team.None"/> = automatisch, also abwechselnd Blau und Grün;
    /// <c>null</c> = bleibt Zuschauer).
    /// </summary>
    public TestClient Join(string name, Team? team = Team.None)
    {
        var client = new TestClient(this, name);
        var player = Match.AddPlayer(Guid.NewGuid(), name, p =>
        {
            client.PlayerId = p.Id;
            _byPlayer[p.Id] = client;
        });
        _clients.Add(client);
        if (team is { } wish) Assert.Null(Match.SelectTeam(player, wish));
        return client;
    }

    public void Leave(TestClient client)
    {
        Match.RemovePlayer(client.PlayerId);
        _byPlayer.Remove(client.PlayerId);
        _clients.Remove(client);
    }

    public ServerPlayer ServerPlayer(TestClient client) => Match.FindPlayer(client.PlayerId)!;

    /// <summary>Setzt einen Spieler auf dem Server an eine Stelle (der Client korrigiert sich beim nächsten Snapshot).</summary>
    public void Teleport(TestClient client, Vec3 feet)
    {
        var p = ServerPlayer(client);
        p.State = new PlayerState { Position = feet, OnGround = true };
        p.Remember(p.LastProcessedTick, p.State, p.LastInput);
    }

    void IMatchOutput.Send(byte playerId, NetWriter message, Delivery delivery)
    {
        if (_byPlayer.TryGetValue(playerId, out var client)) client.ToClient.Enqueue(this, message.ToArray(), delivery);
    }

    public void Frame()
    {
        Time += FrameSeconds;
        foreach (var c in _clients.ToList()) c.ToServer.DeliverDue(Time, data => Match.HandleMessage(c.PlayerId, data, 0, data.Length));
        Match.Step();
        foreach (var c in _clients.ToList())
        {
            c.ToClient.DeliverDue(Time, data => c.Session.HandleMessage(data, 0, data.Length));
            c.Session.Update();
            c.Tick();
        }
    }

    public void Run(double seconds, Action? eachFrame = null)
    {
        var frames = (int)Math.Round(seconds / FrameSeconds);
        for (var i = 0; i < frames; i++)
        {
            Frame();
            eachFrame?.Invoke();
        }
    }

    /// <summary>Läuft, bis die Bedingung gilt (höchstens <paramref name="maxSeconds"/>).</summary>
    public bool RunUntil(Func<bool> condition, double maxSeconds = 5)
    {
        for (var t = 0.0; t < maxSeconds; t += FrameSeconds)
        {
            if (condition()) return true;
            Frame();
        }
        return condition();
    }
}

/// <summary>Eine Richtung einer simulierten Verbindung.</summary>
internal sealed class SimulatedLink
{
    private readonly List<(double Due, long Order, byte[] Data, Delivery Delivery)> _queue = new();
    private long _sent;
    private double _lastReliableDue;
    private long _lastSequenced = -1;

    public int Delivered { get; private set; }

    public void Enqueue(SimulatedNetwork net, byte[] data, Delivery delivery)
    {
        var order = _sent++;
        var due = net.Time + net.OneWayLatency + net.Random.NextDouble() * net.Jitter;
        if (delivery == Delivery.ReliableOrdered)
        {
            while (net.Random.NextDouble() < net.Loss) due += 2 * net.OneWayLatency + 0.02; // verloren, neu gesendet
            due = Math.Max(due, _lastReliableDue); // geordnet: nie vor einer früheren zuverlässigen Nachricht
            _lastReliableDue = due;
        }
        else if (net.Random.NextDouble() < net.Loss)
        {
            return;
        }
        _queue.Add((due, order, data, delivery));
    }

    public void DeliverDue(double time, Action<byte[]> deliver)
    {
        var due = _queue.Where(p => p.Due <= time).OrderBy(p => p.Due).ThenBy(p => p.Order).ToList();
        foreach (var p in due)
        {
            _queue.Remove(p);
            if (p.Delivery == Delivery.Sequenced)
            {
                if (p.Order < _lastSequenced) continue; // älter als der zuletzt zugestellte Snapshot
                _lastSequenced = p.Order;
            }
            Delivered++;
            deliver(p.Data);
        }
    }
}

/// <summary>Ein Client im simulierten Netz: eine echte <see cref="ClientSession"/> plus Eingaben aus dem Test.</summary>
internal sealed class TestClient : IMessageSender
{
    private readonly SimulatedNetwork _net;
    private readonly List<Func<TestClient, BlockEditResult>> _afterTick = new();

    public TestClient(SimulatedNetwork net, string name)
    {
        _net = net;
        Name = name;
        var content = net.Content;
        Session = new ClientSession(content.Registry, content.Movement,
            map => map == content.MapName ? content.MapFileBytes : null, this, () => net.Time, content.Combat);
        Session.EditRejected += result => Rejected.Add(result);
        Session.CombatEvent += e => Events.Add(e);
        Session.MatchEvent += e => MatchEvents.Add(e);
    }

    public string Name { get; }
    public byte PlayerId { get; set; }
    public ClientSession Session { get; }
    public SimulatedLink ToServer { get; } = new();
    public SimulatedLink ToClient { get; } = new();

    /// <summary>Eingabe, die jeden Tick geschickt wird.</summary>
    public PlayerInput Input;

    /// <summary>So viele Ticks pro Frame (2 = ein manipulierter Client, der doppelt so schnell sendet).</summary>
    public int TicksPerFrame { get; set; } = 1;

    public List<BlockEditResult> Rejected { get; } = new();
    public List<BlockEditResult> LocalResults { get; } = new();

    /// <summary>Kampf-Ereignisse, die beim Client angekommen sind.</summary>
    public List<WorldEvent> Events { get; } = new();

    /// <summary>Team-Ereignisse (Teamwechsel, Stand des Matches, Ablehnungen, Kills/Tode), die beim Client angekommen sind.</summary>
    public List<WorldEvent> MatchEvents { get; } = new();

    /// <summary>Plätze der Waffen, die laut Vorhersage gefeuert haben.</summary>
    public List<int> Fired { get; } = new();

    /// <summary>So viele Ticks noch zusätzlich Feuern drücken (1 = ein einzelner Druck).</summary>
    public int FireTicks { get; set; }

    /// <summary>Anzeige des anderen Spielers in dem Moment, in dem zuletzt gefeuert wurde (für Lag-Ausgleich-Tests).</summary>
    public Dictionary<byte, PlayerSnapshot> SeenAtLastShot { get; } = new();

    public PlayerState State => Session.Prediction.State;

    /// <summary>Synchronisiert, im Team und vom Server gespawnt (der erste Snapshot mit Leben hat die Vorhersage an den Spawn gesetzt).</summary>
    public bool Ready => Session.Phase == ClientPhase.Playing && Session.Alive;

    void IMessageSender.Send(NetWriter message, Delivery delivery) => ToServer.Enqueue(_net, message.ToArray(), delivery);

    /// <summary>Nach dem nächsten Tick ausführen (wie der echte Client: erst bewegen, dann abbauen/bauen).</summary>
    public void AfterNextTick(Func<TestClient, BlockEditResult> action) => _afterTick.Add(action);

    public void Tick()
    {
        if (Session.Phase != ClientPhase.Playing) return;
        for (var i = 0; i < TicksPerFrame; i++)
        {
            var input = Input;
            if (FireTicks > 0)
            {
                FireTicks--;
                input.Buttons |= InputButtons.Fire;
            }
            if (input.Has(InputButtons.Fire))
                foreach (var r in Session.RemotePlayers.Values) SeenAtLastShot[r.Id] = r.Current;
            var fired = Session.Tick(input);
            if (fired != 0) Fired.Add(fired);
        }
        foreach (var action in _afterTick) LocalResults.Add(action(this));
        _afterTick.Clear();
    }

    public void LookAt(Vec3 target) => (Input.Yaw, Input.Pitch) = TestArena.Aim(State.Position, target);

    /// <summary>Strahl aus dem Auge wie im echten Client.</summary>
    public bool Target(out VoxelHit hit)
    {
        var eye = State.Position + new Vec3(0, Session.Movement.EyeHeight(State.Crouching), 0);
        return VoxelRaycast.Cast(Session.World, eye, PlayerMovement.LookDirection(Input.Yaw, Input.Pitch), BlockEditing.Reach, out hit);
    }

    public BlockEditResult RemoveTarget() =>
        Target(out var hit) ? Session.RequestRemove(hit.X, hit.Y, hit.Z, hit.Distance) : BlockEditResult.OutOfReach;

    public BlockEditResult PlaceAtTarget(byte id) =>
        Target(out var hit) ? Session.RequestPlace(hit.PlaceX, hit.PlaceY, hit.PlaceZ, id, hit.Distance) : BlockEditResult.OutOfReach;
}
