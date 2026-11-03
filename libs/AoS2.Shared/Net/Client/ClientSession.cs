using System;
using System.Collections.Generic;
using System.IO;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net.Client
{
    public enum ClientPhase
    {
        /// <summary>Verbunden, wartet auf <see cref="Welcome"/>.</summary>
        Connecting,

        /// <summary>Map geladen, empfängt die Änderungen seit Matchbeginn.</summary>
        Syncing,

        Playing,
        Disconnected,
    }

    /// <summary>Schickt Nachrichten an den Server. Umgesetzt vom Transport (LiteNetLib) oder in Tests von einem simulierten Netz.</summary>
    public interface IMessageSender
    {
        void Send(NetWriter message, Delivery delivery);
    }

    /// <summary>Ein anderer Spieler, so wie der Client ihn darstellt.</summary>
    public sealed class RemotePlayer
    {
        internal readonly InterpolationBuffer<PlayerSnapshot> Samples = new InterpolationBuffer<PlayerSnapshot>();

        public RemotePlayer(byte id, string name)
        {
            Id = id;
            Name = name;
        }

        public byte Id { get; }
        public string Name { get; }

        /// <summary>Interpolierter Zustand zum Darstellungszeitpunkt (nach <see cref="ClientSession.Update"/>).</summary>
        public PlayerSnapshot Current { get; internal set; }

        /// <summary>Erst wenn ein Snapshot mit diesem Spieler kam, gibt es etwas zu zeigen.</summary>
        public bool HasState { get; internal set; }
    }

    /// <summary>
    /// Die Client-Seite des Netzspiels, unabhängig vom Transport: verarbeitet Nachrichten des Servers,
    /// rechnet die Vorhersage des eigenen Spielers, schickt Eingaben und Blockänderungen und interpoliert
    /// andere Spieler und fallende Blöcke. Unity zeigt nur an, was hier steht; Tests nutzen dieselbe Klasse.
    /// </summary>
    public sealed class ClientSession
    {
        /// <summary>Andere Spieler werden um 6 Ticks (100 ms, drei Snapshots) verzögert gezeigt.</summary>
        public const double InterpolationDelayTicks = 6;

        private readonly Func<string, byte[]> _readMapFile;
        private readonly IMessageSender _sender;
        private readonly Func<double> _clock;
        private readonly NetWriter _writer = new NetWriter(256);
        private readonly InputPacket _inputPacket = new InputPacket();
        private readonly Dictionary<byte, RemotePlayer> _players = new Dictionary<byte, RemotePlayer>();
        private readonly List<WorldEvent> _events = new List<WorldEvent>();
        private readonly Dictionary<BlockPos, byte> _blockDamage = new Dictionary<BlockPos, byte>();
        private ushort _editSeq;
        private Vec3 _correction;

        /// <param name="readMapFile">Liefert die Bytes von <c>maps/&lt;name&gt;.aos2map</c> oder <c>null</c>, wenn es sie nicht gibt.</param>
        /// <param name="clock">Lokale Zeit in Sekunden (Unity: <c>Time.realtimeSinceStartupAsDouble</c>).</param>
        /// <param name="combat">Kampfregeln und Waffen; <c>null</c> = ohne Waffen.</param>
        public ClientSession(BlockRegistry registry, MovementSettings movement, Func<string, byte[]> readMapFile, IMessageSender sender, Func<double> clock,
            CombatSettings combat = null)
        {
            Registry = registry;
            Movement = movement;
            Combat = combat;
            _readMapFile = readMapFile;
            _sender = sender;
            _clock = clock;
        }

        public BlockRegistry Registry { get; }
        public MovementSettings Movement { get; }
        public CombatSettings Combat { get; }

        public ClientPhase Phase { get; private set; } = ClientPhase.Connecting;

        /// <summary>Warum die Verbindung endete (für die Anzeige), sonst <c>null</c>.</summary>
        public string Error { get; private set; }

        public byte PlayerId { get; private set; }
        public string PlayerName { get; private set; }
        public string MapName { get; private set; }
        public VoxelWorld World { get; private set; }
        public MapMetadata Metadata { get; private set; }
        public ClientPrediction Prediction { get; private set; }
        public PredictedBlocks Blocks { get; private set; }
        public NetFallingGroups Falling { get; private set; }
        public ServerClock Clock { get; private set; }
        public NetProjectiles Projectiles { get; private set; }

        /// <summary>Eigene Lebenspunkte laut Server (0 = tot).</summary>
        public int Health { get; private set; }

        public bool Alive => Health > 0;

        /// <summary>Tot: Ticks bis zum Respawn (aus dem letzten Snapshot).</summary>
        public int RespawnTicks { get; private set; }

        /// <summary>Zählt die eigenen Leben (+1 bei jedem Respawn).</summary>
        public int Life { get; private set; }

        /// <summary>Schaden je beschädigtem Block (1 … 255), für die Risse.</summary>
        public IReadOnlyDictionary<BlockPos, byte> BlockDamage => _blockDamage;

        /// <summary>Kampf-Ereignisse für Anzeige und Effekte: Schaden, Kill, Schüsse anderer, Geschosse, Blockschaden.</summary>
        public event Action<WorldEvent> CombatEvent;

        public IReadOnlyDictionary<byte, RemotePlayer> RemotePlayers => _players;

        public uint LastSnapshotTick { get; private set; }
        public int SnapshotsReceived { get; private set; }

        /// <summary>Darstellungszeitpunkt der anderen Spieler in Server-Ticks (nach <see cref="Update"/>).</summary>
        public double RenderTick { get; private set; }

        /// <summary>Der Server hat eine eigene Blockänderung abgelehnt (sie ist schon wieder zurückgenommen).</summary>
        public event Action<BlockEditResult> EditRejected;

        /// <summary>Verschiebung des eigenen Spielers durch Abgleiche seit dem letzten Aufruf. Die Anzeige gleicht sie weich aus.</summary>
        public Vec3 TakeCorrection()
        {
            var c = _correction;
            _correction = Vec3.Zero;
            return c;
        }

        // ---- Empfangen ----

        public void HandleMessage(byte[] data, int offset, int count)
        {
            if (Phase == ClientPhase.Disconnected || count == 0) return;
            try
            {
                var r = new NetReader(data, offset, count);
                var type = (MessageType)r.ReadByte();
                switch (type)
                {
                    case MessageType.Welcome:
                        OnWelcome(Welcome.Read(r));
                        break;
                    case MessageType.Snapshot:
                        if (World != null) OnSnapshot(Snapshot.Read(r));
                        break;
                    case MessageType.Events:
                        if (World == null) throw new ProtocolException("Ereignisse vor der Begrüßung.");
                        _events.Clear();
                        WorldEvents.Read(r, _events);
                        foreach (var e in _events) OnEvent(e);
                        break;
                    default:
                        throw new ProtocolException($"Unbekannte Nachricht {(byte)type}.");
                }
            }
            catch (ProtocolException e)
            {
                Fail("Ungültige Nachricht vom Server: " + e.Message);
            }
        }

        /// <summary>Die Verbindung ist zu (vom Transport gemeldet).</summary>
        public void OnDisconnected(string message)
        {
            if (Phase == ClientPhase.Disconnected) return;
            Fail(message);
        }

        private void Fail(string message)
        {
            Phase = ClientPhase.Disconnected;
            Error = message;
        }

        private void OnWelcome(Welcome welcome)
        {
            if (World != null) throw new ProtocolException("Doppelte Begrüßung.");
            if (welcome.TickRate != Movement.TickRate)
            {
                Fail($"Der Server rechnet mit {welcome.TickRate} Ticks/s, das Spiel mit {Movement.TickRate}.");
                return;
            }

            var file = _readMapFile(welcome.MapName);
            if (file == null)
            {
                Fail($"Die Map \"{welcome.MapName}\" fehlt. Bitte das Spiel im Launcher aktualisieren.");
                return;
            }
            if (!MapChecksum.AreEqual(MapChecksum.Compute(file), welcome.MapChecksum))
            {
                Fail($"Die Map \"{welcome.MapName}\" ist eine andere Version als auf dem Server. Bitte das Spiel im Launcher aktualisieren.");
                return;
            }
            GameMap map;
            try
            {
                map = MapFile.Read(new MemoryStream(file), Registry);
            }
            catch (InvalidDataException e)
            {
                Fail($"Die Map \"{welcome.MapName}\" lässt sich nicht laden: {e.Message}");
                return;
            }

            PlayerId = welcome.PlayerId;
            PlayerName = welcome.PlayerName;
            MapName = welcome.MapName;
            World = map.World;
            Metadata = map.Metadata;
            Blocks = new PredictedBlocks(World);
            Falling = new NetFallingGroups(Blocks);
            Prediction = new ClientPrediction(World, Movement, welcome.Spawn, Combat);
            Projectiles = new NetProjectiles(World, Combat, Movement.DeltaTime);
            Health = Combat?.Health ?? 100;
            World.BlockChanged += (x, y, z, _, __) => _blockDamage.Remove(new BlockPos(x, y, z));
            Clock = new ServerClock(welcome.TickRate);
            LastSnapshotTick = welcome.ServerTick;
            Phase = ClientPhase.Syncing;
        }

        private void OnSnapshot(Snapshot s)
        {
            if (SnapshotsReceived > 0 && s.ServerTick <= LastSnapshotTick) return;
            LastSnapshotTick = s.ServerTick;
            SnapshotsReceived++;
            Clock.OnSnapshot(s.ServerTick, _clock());

            var c = Prediction.Reconcile(s.AckInputTick, s.Own, s.OwnWeapons);
            _correction = _correction + c;
            Health = s.OwnHealth;
            RespawnTicks = s.OwnRespawnTicks;
            Life = s.OwnLife;

            foreach (var p in s.Players)
                if (_players.TryGetValue(p.Id, out var remote))
                    remote.Samples.Add(s.ServerTick, p);
            Falling.OnSnapshot(s.ServerTick, s.Falling);
        }

        private void OnEvent(WorldEvent e)
        {
            switch (e.Type)
            {
                case WorldEventType.BlockSet:
                    Blocks.ApplyServer(e.X, e.Y, e.Z, e.BlockId);
                    break;
                case WorldEventType.EditResult:
                    Blocks.Resolve(e.Seq);
                    if (e.Result != BlockEditResult.Ok) EditRejected?.Invoke(e.Result);
                    break;
                case WorldEventType.FallStart:
                    Falling.Start(e.GroupId, e.Blocks, e.Fallen, e.Velocity, LastSnapshotTick);
                    break;
                case WorldEventType.FallLand:
                    Falling.Land(e.GroupId, e.Offset);
                    break;
                case WorldEventType.PlayerJoined:
                    if (e.PlayerId != PlayerId) _players[e.PlayerId] = new RemotePlayer(e.PlayerId, e.Name);
                    break;
                case WorldEventType.PlayerLeft:
                    _players.Remove(e.PlayerId);
                    break;
                case WorldEventType.SyncComplete:
                    if (Phase == ClientPhase.Syncing) Phase = ClientPhase.Playing;
                    break;
                case WorldEventType.BlockDamaged:
                    if (e.Level == 0) _blockDamage.Remove(new BlockPos(e.X, e.Y, e.Z));
                    else _blockDamage[new BlockPos(e.X, e.Y, e.Z)] = e.Level;
                    CombatEvent?.Invoke(e);
                    break;
                case WorldEventType.PlayerDamaged:
                    if (e.PlayerId == PlayerId) Health = e.Health;
                    CombatEvent?.Invoke(e);
                    break;
                case WorldEventType.PlayerKilled:
                    if (e.PlayerId == PlayerId)
                    {
                        Health = 0;
                        RespawnTicks = Combat?.RespawnTicks ?? 0;
                    }
                    CombatEvent?.Invoke(e);
                    break;
                case WorldEventType.ShotFired:
                    CombatEvent?.Invoke(e);
                    break;
                case WorldEventType.ProjectileSpawned:
                    Projectiles.Spawn(e.ProjectileId, e.Weapon, e.PlayerId, e.Tick, e.Position, e.Vector);
                    CombatEvent?.Invoke(e);
                    break;
                case WorldEventType.ProjectileExploded:
                    Projectiles.Explode(e.ProjectileId);
                    CombatEvent?.Invoke(e);
                    break;
            }
        }

        // ---- Senden ----

        /// <summary>
        /// Ein Simulationsschritt des eigenen Spielers: vorhersagen und die Eingabe an den Server schicken.
        /// Liefert den Platz der Waffe, die in diesem Tick feuert (0 = keine), damit die Anzeige Mündungsfeuer zeigen kann.
        /// Beim Feuern geht der gerade gezeigte Zeitpunkt (<see cref="RenderTick"/>) mit: Gegen ihn prüft der Server den Treffer.
        /// Tot zählen nur Blickrichtung und Waffenwahl (wie auf dem Server).
        /// </summary>
        public int Tick(PlayerInput input)
        {
            if (Phase != ClientPhase.Playing) return 0;
            if (!Alive) input.Buttons = InputButtons.None;
            if (input.Has(InputButtons.Fire)) input.ViewTick = RenderTick;
            Prediction.Step(input, out var fired);
            Prediction.FillInputPacket(_inputPacket);
            _writer.Clear();
            _inputPacket.Write(_writer);
            _sender.Send(_writer, Delivery.Unreliable);
            return fired;
        }

        /// <summary>Block abbauen: sofort zeigen, beim Server anfragen. Gehört zum zuletzt gerechneten Tick.</summary>
        public BlockEditResult RequestRemove(int x, int y, int z, float distance)
        {
            if (Phase != ClientPhase.Playing) return BlockEditResult.OutOfReach;
            if (!Alive) return BlockEditResult.Dead;
            var result = BlockEditing.CanRemove(World, Registry, x, y, z, distance);
            if (result == BlockEditResult.Ok) SendEdit(BlockEditKind.Remove, x, y, z, BlockRegistry.Air);
            return result;
        }

        public BlockEditResult RequestPlace(int x, int y, int z, byte id, float distance)
        {
            if (Phase != ClientPhase.Playing) return BlockEditResult.OutOfReach;
            if (!Alive) return BlockEditResult.Dead;
            var result = BlockEditing.CanPlace(World, Registry, x, y, z, id, distance, Prediction.State, Movement);
            if (result == BlockEditResult.Ok) SendEdit(BlockEditKind.Place, x, y, z, id);
            return result;
        }

        private void SendEdit(BlockEditKind kind, int x, int y, int z, byte id)
        {
            var seq = ++_editSeq;
            Blocks.Predict(seq, x, y, z, id);
            var request = new BlockEditRequest { Seq = seq, Tick = Prediction.Tick, Kind = kind, X = x, Y = y, Z = z, BlockId = id };
            _writer.Clear();
            request.Write(_writer);
            _sender.Send(_writer, Delivery.ReliableOrdered);
        }

        // ---- Darstellung ----

        /// <summary>Einmal pro Bild: Darstellungszeitpunkt bestimmen, andere Spieler und fallende Blöcke interpolieren.</summary>
        public void Update()
        {
            if (World == null) return;
            var serverTick = Clock.IsInitialized ? Clock.ServerTick(_clock()) : LastSnapshotTick;
            RenderTick = serverTick - InterpolationDelayTicks;
            foreach (var p in _players.Values)
            {
                if (!p.Samples.Sample(RenderTick, LerpPlayer, out var s)) continue;
                p.Current = s;
                p.HasState = true;
            }
            Falling.UpdateRender(serverTick);
            Projectiles.UpdateRender(serverTick);
        }

        private static readonly Func<PlayerSnapshot, PlayerSnapshot, float, PlayerSnapshot> LerpPlayer = PlayerSnapshot.Lerp;
    }
}
