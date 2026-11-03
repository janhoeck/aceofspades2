using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;
using Microsoft.Extensions.Logging;

namespace AoS2.Server;

/// <summary>Wohin der Server Nachrichten schickt. Umgesetzt vom Transport (LiteNetLib) oder in Tests von einem simulierten Netz.</summary>
public interface IMatchOutput
{
    void Send(byte playerId, NetWriter message, Delivery delivery);
}

/// <summary>
/// Die autoritative Simulation einer Runde, ohne Netzwerk-Code: Spieler, Eingaben, Blockänderungen,
/// fallende Blöcke, Kampf (siehe Match.Combat.cs) und Snapshots. Ein Aufruf von <see cref="Step"/> = ein Tick (60/s).
/// </summary>
public sealed partial class Match
{
    /// <summary>Alle 2 Ticks ein Snapshot: 30 pro Sekunde.</summary>
    public const int SnapshotInterval = 2;

    /// <summary>Höchstens so viele Eingaben rechnet der Server pro Spieler und Tick (holt nach einem Ruckler auf).</summary>
    internal const int MaxStepsPerTick = 4;

    /// <summary>Guthaben an Eingaben: +1 pro Tick, höchstens so viel. Mehr als eine Eingabe pro Tick geht auf Dauer nicht.</summary>
    internal const int MaxBudget = 8;

    /// <summary>Mehr angekommene, ungerechnete Eingaben (1 s) werden verworfen; der Client wird dann zurückgesetzt.</summary>
    internal const int MaxQueuedInputs = 60;

    /// <summary>Wartet eine Blockaktion so lange auf ihre Eingabe, wird sie trotzdem geprüft (mit dem aktuellen Zustand).</summary>
    internal const int EditWaitTicks = 30;

    /// <summary>Unzuverlässige Pakete müssen in eine UDP-Nachricht passen (LiteNetLib startet mit 1024 Byte).</summary>
    internal const int MaxSnapshotBytes = 1000;

    /// <summary>Ereignisse werden in Nachrichten bis etwa zu dieser Größe gebündelt.</summary>
    private const int EventFlushBytes = 32 * 1024;

    private readonly GameContent _content;
    private readonly IMatchOutput _output;
    private readonly ILogger _log;
    private readonly int _maxPlayers;
    private readonly SortedDictionary<byte, ServerPlayer> _players = new();
    private readonly Dictionary<byte, NetWriter> _events = new();
    private readonly Dictionary<long, byte> _changes = new();
    private readonly NetWriter _scratch = new(1024);
    private readonly NetWriter _snapshotWriter = new(MaxSnapshotBytes);
    private readonly Snapshot _snapshot = new();
    private int _spawnCounter;

    public Match(GameContent content, IMatchOutput output, ILogger log, int maxPlayers = ServerOptions.MaxPlayersLimit)
    {
        _content = content;
        _output = output;
        _log = log;
        _maxPlayers = Math.Min(maxPlayers, ServerOptions.MaxPlayersLimit);
        var map = content.LoadMap();
        World = map.World;
        Metadata = map.Metadata;
        Falling = new FallingBlocks(World, content.Movement.Gravity, content.Movement.MaxFallSpeed);
        BlockDamage = new BlockDamage(World, content.Registry);
        _pose = new PlayerPose(content.Rig);

        // Jede Änderung seit Matchbeginn merken: Wer später beitritt, bekommt genau diese Liste.
        World.BlockChanged += (x, y, z, _, newId) => _changes[Key(x, y, z)] = newId;
        Falling.Started += group =>
        {
            _scratch.Clear();
            WorldEvents.WriteFallStart(_scratch, group.Id, group.Blocks, 0, 0);
            Broadcast(_scratch);
        };
        Falling.Landed += (group, offset) =>
        {
            _scratch.Clear();
            WorldEvents.WriteFallLand(_scratch, group.Id, offset);
            Broadcast(_scratch);
        };
    }

    public VoxelWorld World { get; }
    public MapMetadata Metadata { get; }
    public FallingBlocks Falling { get; }
    public GameContent Content => _content;

    /// <summary>Anzahl der gerechneten Server-Ticks.</summary>
    public uint Tick { get; private set; }

    public IReadOnlyCollection<ServerPlayer> Players => _players.Values;

    public bool IsFull => _players.Count >= _maxPlayers;

    /// <summary>Blöcke, die sich seit Matchbeginn geändert haben.</summary>
    public int ChangeCount => _changes.Count;

    public ServerPlayer? FindPlayer(byte id) => _players.GetValueOrDefault(id);

    // ---- Beitreten und Verlassen ----

    /// <summary>
    /// Nimmt einen Spieler auf und schickt ihm alles für den Start: Begrüßung (Map-ID, Prüfsumme, Spawn),
    /// alle Blockänderungen seit Matchbeginn, fallende Gruppen und die anderen Spieler.
    /// </summary>
    public ServerPlayer AddPlayer(Guid accountId, string name, Action<ServerPlayer>? onCreated = null)
    {
        if (IsFull) throw new InvalidOperationException("Der Server ist voll.");
        byte id = 0;
        while (_players.ContainsKey(id)) id++;

        var spawn = new PlayerState { Position = Spawns.Pick(World, Metadata, _spawnCounter++) };
        var player = new ServerPlayer(id, accountId, name, spawn)
        {
            Health = _content.Combat.Health,
            WeaponsState = WeaponState.Full(_content.Combat),
            AnimationState = PlayerAnimator.Start(_content.Rig, Tick),
        };
        player.RecordView(Tick, ViewOf(player));
        onCreated?.Invoke(player); // der Transport ordnet die Verbindung zu, bevor die erste Nachricht rausgeht

        var welcome = new Welcome
        {
            PlayerId = id,
            PlayerName = name,
            ServerTick = Tick,
            TickRate = (byte)_content.Movement.TickRate,
            SnapshotInterval = SnapshotInterval,
            MapName = _content.MapName,
            MapChecksum = _content.MapChecksum,
            Spawn = spawn,
        };
        _scratch.Clear();
        welcome.Write(_scratch);
        _output.Send(id, _scratch, Delivery.ReliableOrdered);

        // Erst alle anderen über den Neuen informieren, dann ihn selbst aufnehmen und synchronisieren.
        _scratch.Clear();
        WorldEvents.WritePlayerJoined(_scratch, id, name);
        Broadcast(_scratch);

        _players.Add(id, player);
        var events = new NetWriter(4096);
        WorldEvents.WriteHeader(events);
        _events.Add(id, events);

        foreach (var (key, blockId) in _changes)
        {
            Unpack(key, out var x, out var y, out var z);
            BeginEvent(id);
            WorldEvents.WriteBlockSet(_events[id], x, y, z, blockId);
        }
        foreach (var group in Falling.Groups)
        {
            BeginEvent(id);
            WorldEvents.WriteFallStart(_events[id], group.Id, group.Blocks, group.Fallen, group.Velocity);
        }
        foreach (var other in _players.Values)
        {
            BeginEvent(id);
            WorldEvents.WritePlayerJoined(_events[id], other.Id, other.Name);
        }
        foreach (var pos in BlockDamage.All())
        {
            BeginEvent(id);
            WorldEvents.WriteBlockDamaged(_events[id], pos.X, pos.Y, pos.Z, BlockDamage.Level(pos.X, pos.Y, pos.Z));
        }
        foreach (var projectile in _projectiles)
        {
            BeginEvent(id);
            WorldEvents.WriteProjectileSpawned(_events[id], projectile.Projectile, Tick);
        }
        BeginEvent(id);
        WorldEvents.WriteSyncComplete(_events[id]);
        FlushEvents(id);

        _log.LogInformation("{Name} ist beigetreten (Spieler {Id}, {Count} Spieler, {Changes} Blockänderungen synchronisiert)",
            name, id, _players.Count, _changes.Count);
        return player;
    }

    public void RemovePlayer(byte id)
    {
        if (!_players.Remove(id, out var player)) return;
        _events.Remove(id);
        _scratch.Clear();
        WorldEvents.WritePlayerLeft(_scratch, id);
        Broadcast(_scratch);
        _log.LogInformation("{Name} hat das Spiel verlassen ({Count} Spieler)", player.Name, _players.Count);
    }

    // ---- Empfangen ----

    /// <summary>Verarbeitet eine Nachricht eines Spielers. <c>false</c> bei einer kaputten Nachricht (der Aufrufer trennt dann).</summary>
    public bool HandleMessage(byte playerId, byte[] data, int offset, int count)
    {
        if (!_players.TryGetValue(playerId, out var player)) return true;
        try
        {
            var r = new NetReader(data, offset, count);
            var type = (MessageType)r.ReadByte();
            switch (type)
            {
                case MessageType.Input:
                    ReceiveInputs(player, InputPacket.Read(r));
                    return true;
                case MessageType.BlockEdit:
                    if (player.Edits.Count >= 64) throw new ProtocolException("Zu viele offene Blockaktionen.");
                    player.Edits.Add(new PendingEdit(BlockEditRequest.Read(r), Tick));
                    return true;
                default:
                    throw new ProtocolException($"Unbekannte Nachricht {(byte)type}.");
            }
        }
        catch (ProtocolException e)
        {
            _log.LogWarning("Ungültige Nachricht von {Name}: {Message}", player.Name, e.Message);
            return false;
        }
    }

    private static void ReceiveInputs(ServerPlayer p, InputPacket packet)
    {
        var first = packet.NewestTick - (uint)packet.Inputs.Count + 1;
        for (var i = 0; i < packet.Inputs.Count; i++)
        {
            var tick = first + (uint)i;
            if (tick <= p.LastReceivedTick) continue; // schon da (Wiederholung aus einem früheren Paket)
            var input = packet.Inputs[i];

            if (tick - p.LastReceivedTick > MaxQueuedInputs)
            {
                // Riesige Lücke: alles Alte verwerfen statt sie aufzufüllen.
                p.DroppedInputs += p.Inputs.Count;
                p.Inputs.Clear();
                p.LastProcessedTick = p.LastReceivedTick = tick - 1;
                p.Remember(p.LastProcessedTick, p.State, p.LastInput);
            }

            // Fehlen Eingaben ganz (alle Kopien verloren), die neue an ihrer Stelle verwenden: Der Client korrigiert sich danach.
            while (p.LastReceivedTick + 1 < tick) Enqueue(p, input);
            Enqueue(p, input);
        }
    }

    private static void Enqueue(ServerPlayer p, PlayerInput input)
    {
        p.Inputs.Enqueue(input);
        p.LastReceivedTick++;
        if (p.Inputs.Count <= MaxQueuedInputs) return;
        // Zu viele auf einmal (z. B. ein manipulierter Client sendet zu schnell): die älteste ungerechnet verwerfen.
        p.Inputs.Dequeue();
        p.LastProcessedTick++;
        p.Remember(p.LastProcessedTick, p.State, p.LastInput);
        p.DroppedInputs++;
    }

    // ---- Simulation ----

    /// <summary>Ein Server-Tick: Eingaben und Blockaktionen aller Spieler, fallende Blöcke, Snapshots, Ereignisse.</summary>
    public void Step()
    {
        Tick++;
        RespawnDue();
        foreach (var p in _players.Values)
        {
            p.Budget = Math.Min(p.Budget + 1, MaxBudget);
            for (var steps = 0; steps < MaxStepsPerTick && p.Budget > 0 && p.Inputs.Count > 0; steps++)
            {
                var input = p.Inputs.Dequeue();
                if (!p.Alive) input.Buttons = InputButtons.None; // Tote schauen nur zu (wie auf dem Client)
                p.Budget--;
                p.LastProcessedTick++;
                p.State = PlayerMovement.Step(p.State, input, World, _content.Movement);
                var fired = WeaponSim.Step(ref p.WeaponsState, input, _content.Combat);
                p.LastInput = input;
                p.Remember(p.LastProcessedTick, p.State, input);
                if (fired != 0) Fire(p, fired, input);
                ProcessEdits(p);
            }
            ProcessEdits(p);
        }

        StepProjectiles();
        Falling.Tick(_content.Movement.DeltaTime);

        // Pose und Ansicht jedes Spielers zu diesem Tick merken: Gegen sie prüft der Lag-Ausgleich spätere Schüsse.
        foreach (var p in _players.Values)
        {
            PlayerAnimator.Update(ref p.AnimationState, _content.Rig, p.State, _content.Movement, Tick);
            p.RecordView(Tick, ViewOf(p));
        }

        if (Tick % SnapshotInterval == 0)
            foreach (var p in _players.Values) SendSnapshot(p);
        foreach (var id in _players.Keys) FlushEvents(id);
    }

    /// <summary>Blockaktionen in Reihenfolge prüfen, sobald ihr Eingabe-Tick gerechnet ist (oder sie zu lange warten).</summary>
    private void ProcessEdits(ServerPlayer p)
    {
        while (p.Edits.Count > 0)
        {
            var edit = p.Edits[0];
            if (edit.Request.Tick > p.LastProcessedTick && Tick - edit.ReceivedAt < EditWaitTicks) return;
            p.Edits.RemoveAt(0);
            ApplyEdit(p, edit.Request);
        }
    }

    private void ApplyEdit(ServerPlayer p, BlockEditRequest e)
    {
        var result = CheckEdit(p, e);
        if (result == BlockEditResult.Ok)
        {
            p.LastEditTick = e.Tick;
            var id = e.Kind == BlockEditKind.Remove ? BlockRegistry.Air : e.BlockId;
            World.SetBlock(e.X, e.Y, e.Z, id);
            _scratch.Clear();
            WorldEvents.WriteBlockSet(_scratch, e.X, e.Y, e.Z, id);
            Broadcast(_scratch);
            if (e.Kind == BlockEditKind.Remove) Falling.CheckAfterRemoval(e.X, e.Y, e.Z);
        }
        else
        {
            _log.LogDebug("Blockaktion von {Name} bei ({X}, {Y}, {Z}) abgelehnt: {Result}", p.Name, e.X, e.Y, e.Z, result);
        }

        // Die Antwort kommt nach der Änderung: So weiß der Client, was laut Server dort steht, wenn er seine Vorhersage auflöst.
        BeginEvent(p.Id);
        WorldEvents.WriteEditResult(_events[p.Id], e.Seq, result);
    }

    /// <summary>
    /// Prüft eine Blockaktion mit dem Zustand des Spielers zu ihrem Eingabe-Tick: Pause seit der letzten Aktion,
    /// Sichtlinie (der Strahl vom Auge in Blickrichtung trifft genau diesen Block), Reichweite und die Regeln aus
    /// <see cref="BlockEditing"/>. Beim Bauen darf auch kein anderer Spieler im Weg stehen.
    /// </summary>
    private BlockEditResult CheckEdit(ServerPlayer p, BlockEditRequest e)
    {
        if (!p.TryRecall(e.Tick, out var state, out var input))
        {
            // Eingabe nie angekommen oder zu alt: mit dem aktuellen Zustand prüfen.
            if (e.Tick <= p.LastProcessedTick) return BlockEditResult.NoLineOfSight;
            state = p.State;
            input = p.LastInput;
        }
        if (!p.Alive) return BlockEditResult.Dead;
        if (p.LastEditTick is { } last && e.Tick < last + BlockEditing.CooldownTicks) return BlockEditResult.TooFast;

        var movement = _content.Movement;
        var eye = state.Position + new Vec3(0, movement.EyeHeight(state.Crouching), 0);
        if (!VoxelRaycast.Cast(World, eye, PlayerMovement.LookDirection(input.Yaw, input.Pitch), BlockEditing.Reach, out var hit))
            return BlockEditResult.OutOfReach;

        if (e.Kind == BlockEditKind.Remove)
        {
            if (hit.X != e.X || hit.Y != e.Y || hit.Z != e.Z) return BlockEditResult.NoLineOfSight;
            return BlockEditing.CanRemove(World, _content.Registry, e.X, e.Y, e.Z, hit.Distance);
        }

        if (hit.PlaceX != e.X || hit.PlaceY != e.Y || hit.PlaceZ != e.Z) return BlockEditResult.NoLineOfSight;
        var result = BlockEditing.CanPlace(World, _content.Registry, e.X, e.Y, e.Z, e.BlockId, hit.Distance, state, movement);
        if (result != BlockEditResult.Ok) return result;
        foreach (var other in _players.Values)
            if (other != p && PlayerMovement.OverlapsBlock(other.State.Position, movement.HalfWidth, movement.Height(other.State.Crouching), e.X, e.Y, e.Z))
                return BlockEditResult.BlockedByPlayer;
        return BlockEditResult.Ok;
    }

    // ---- Senden ----

    private void SendSnapshot(ServerPlayer to)
    {
        var s = _snapshot;
        s.ServerTick = Tick;
        s.AckInputTick = to.LastProcessedTick;
        s.Own = to.State;
        s.OwnWeapons = to.WeaponsState;
        s.OwnHealth = (byte)to.Health;
        s.OwnRespawnTicks = to.Alive ? (ushort)0 : (ushort)Math.Min(to.RespawnAt - Tick, ushort.MaxValue);
        s.OwnLife = to.Life;
        s.Players.Clear();
        s.Falling.Clear();

        // Andere Spieler, so viele wie ins Paket passen (bis etwa 32 Spieler alle). Bei mehr Spielern kommen sie
        // reihum dran; der Client interpoliert dann über eine längere Lücke.
        var maxPlayers = (MaxSnapshotBytes - Snapshot.HeaderBytes) / Snapshot.MaxPlayerBytes;
        var others = _players.Count - 1;
        var first = others > maxPlayers ? (int)(Tick / SnapshotInterval % (uint)others) : 0;
        var index = 0;
        foreach (var p in _players.Values)
        {
            if (p == to) continue;
            var k = (index++ - first + others) % Math.Max(others, 1);
            if (k < maxPlayers) s.Players.Add(ViewOf(p));
        }

        // So viele fallende Gruppen, wie in das Paket passen (je höchstens 9 Byte). Die übrigen kommen im nächsten Snapshot.
        var room = (MaxSnapshotBytes - Snapshot.HeaderBytes - s.Players.Count * Snapshot.MaxPlayerBytes) / 9;
        var groups = Falling.Groups;
        var take = Math.Min(Math.Min(room, groups.Count), byte.MaxValue);
        var start = groups.Count > take ? (int)(Tick / SnapshotInterval % (uint)groups.Count) : 0;
        for (var i = 0; i < take; i++)
        {
            var g = groups[(start + i) % groups.Count];
            s.Falling.Add(new FallingSnapshot { GroupId = g.Id, Fallen = g.Fallen });
        }

        _snapshotWriter.Clear();
        s.Write(_snapshotWriter);
        _output.Send(to.Id, _snapshotWriter, Delivery.Sequenced);
    }

    /// <summary>Hängt ein fertig geschriebenes Ereignis an die Warteschlange aller Spieler.</summary>
    private void Broadcast(NetWriter singleEvent)
    {
        foreach (var id in _events.Keys)
        {
            BeginEvent(id);
            _events[id].WriteBytes(singleEvent.Buffer, 0, singleEvent.Length);
        }
    }

    /// <summary>Vor jedem Ereignis: Ist die Nachricht schon groß, wird sie zuerst abgeschickt.</summary>
    private void BeginEvent(byte id)
    {
        if (_events[id].Length >= EventFlushBytes) FlushEvents(id);
    }

    private void FlushEvents(byte id)
    {
        var w = _events[id];
        if (w.Length <= 1) return;
        _output.Send(id, w, Delivery.ReliableOrdered);
        w.Clear();
        WorldEvents.WriteHeader(w);
    }

    private static long Key(int x, int y, int z) => ((long)x << 32) | ((long)y << 16) | (uint)z;

    private static void Unpack(long key, out int x, out int y, out int z)
    {
        x = (int)(key >> 32);
        y = (int)((key >> 16) & 0xFFFF);
        z = (int)(key & 0xFFFF);
    }
}
