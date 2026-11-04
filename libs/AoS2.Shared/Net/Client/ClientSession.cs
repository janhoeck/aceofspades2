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

        /// <summary>Team (<see cref="Team.None"/>: wählt noch).</summary>
        public Team Team { get; internal set; }

        public int Kills { get; internal set; }
        public int Deaths { get; internal set; }

        /// <summary>Klasse laut letztem Snapshot (<see cref="PlayerSnapshot.Class"/>, 0 = keine).</summary>
        public byte ClassId => HasState ? Current.Class : (byte)0;
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
        private readonly string _playerRigJson;
        private readonly Dictionary<ushort, BlockEditKind> _pendingEdits = new Dictionary<ushort, BlockEditKind>();
        private ushort _editSeq;
        private Vec3 _correction;
        private int _serverBlocks;

        /// <param name="rules">Eigene Spielregeln aus <c>data/</c>. Der Server schickt beim Beitritt seine (<see cref="MessageType.GameData"/>);
        /// ab dann gelten diese.</param>
        /// <param name="playerRigJson">Spielermodell (Kollisionshöhe je Ausrüstung), um die Regeln des Servers zu lesen.</param>
        /// <param name="readMapFile">Liefert die Bytes von <c>maps/&lt;name&gt;.aos2map</c> oder <c>null</c>, wenn es sie nicht gibt.</param>
        /// <param name="clock">Lokale Zeit in Sekunden (Unity: <c>Time.realtimeSinceStartupAsDouble</c>).</param>
        public ClientSession(BlockRegistry registry, GameRules rules, string playerRigJson, Func<string, byte[]> readMapFile, IMessageSender sender, Func<double> clock)
        {
            Registry = registry;
            Rules = rules;
            Class = rules.Classes.None;
            _playerRigJson = playerRigJson;
            _readMapFile = readMapFile;
            _sender = sender;
            _clock = clock;
        }

        public BlockRegistry Registry { get; }

        /// <summary>Spielregeln: zuerst die eigenen, nach dem Beitritt die des Servers.</summary>
        public GameRules Rules { get; private set; }

        /// <summary>Die Regeln kamen vom Server (sonst rechnet der Client mit den eigenen).</summary>
        public bool HasServerRules { get; private set; }

        /// <summary>Bewegung der eigenen Klasse (Tempo, Kollisionshöhe mit Ausrüstung).</summary>
        public MovementSettings Movement => Class.Movement;

        public CombatSettings Combat => Rules.Combat;
        public PlayerClasses Classes => Rules.Classes;

        // ---- Klassen ----

        /// <summary>Eigene Klasse laut Server (<see cref="PlayerClasses.None"/>, solange man zuschaut).</summary>
        public PlayerClass Class { get; private set; }

        /// <summary>Gewählte Klasse für den nächsten Spawn (<c>null</c> = noch keine gewählt, der Server nimmt dann <c>defaultClass</c>).</summary>
        public PlayerClass NextClass { get; private set; }

        /// <summary>Klasse eines Spielers (eigene oder laut Snapshot), <see cref="PlayerClasses.None"/> wenn unbekannt.</summary>
        public PlayerClass ClassOf(byte playerId)
        {
            if (playerId == PlayerId) return Class;
            return _players.TryGetValue(playerId, out var p) ? Classes.Get(p.ClassId) ?? Classes.None : Classes.None;
        }

        /// <summary>
        /// Klasse für den nächsten Spawn wählen (Regel: der Wechsel gilt beim nächsten Respawn). Zuschauer und Tote erscheinen
        /// gleich mit ihr, wer lebt, behält seine Klasse bis zum nächsten Tod.
        /// </summary>
        public void RequestClass(PlayerClass cls)
        {
            if (Phase != ClientPhase.Playing || cls == null || !Classes.IsSelectable(cls.Id)) return;
            NextClass = cls;
            _writer.Clear();
            new ClassSelectRequest { Class = cls.Id }.Write(_writer);
            _sender.Send(_writer, Delivery.ReliableOrdered);
        }

        /// <summary>
        /// Blockvorrat, wie der Client ihn gerade sieht: der letzte Stand vom Server, verrechnet mit den eigenen Blockaktionen,
        /// die der Server noch nicht beantwortet hat (Bauen −1, Abbauen +1, höchstens <see cref="PlayerClass.Blocks"/>).
        /// </summary>
        public int BlocksLeft
        {
            get
            {
                var blocks = _serverBlocks;
                foreach (var kind in _pendingEdits.Values) blocks += kind == BlockEditKind.Place ? -1 : 1;
                return blocks < 0 ? 0 : blocks > Class.Blocks ? Class.Blocks : blocks;
            }
        }

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

        // ---- Team Deathmatch ----

        /// <summary>Zonen der Map (Spawnzonen, Kampfzone).</summary>
        public MapZones Zones { get; private set; }

        /// <summary>Eigenes Team (<see cref="Team.None"/>: noch keins gewählt, man schaut zu).</summary>
        public Team Team { get; private set; }

        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        /// <summary>Stand des Matches laut Server (Punkte, Phase, Regeln).</summary>
        public MatchInfo Match { get; private set; }

        /// <summary>Das Match ist vorbei (Ergebnis-Anzeige bis zum Neustart): niemand kann kämpfen oder bauen.</summary>
        public bool MatchOver => Match.Phase == MatchPhase.Ended;

        /// <summary>Team-Ereignisse für die Anzeige: Teamwechsel, Stand des Matches, abgelehnter Teamwunsch, Kills/Tode.</summary>
        public event Action<WorldEvent> MatchEvent;

        /// <summary>Spieler im Team (ohne Zuschauer), den eigenen mitgezählt.</summary>
        public int TeamSize(Team team)
        {
            var count = Team == team && team != Team.None ? 1 : 0;
            foreach (var p in _players.Values)
                if (p.Team == team) count++;
            return count;
        }

        /// <summary>Team wählen (<see cref="Team.None"/> = automatisch). Der Server antwortet mit einem Teamwechsel oder einer Ablehnung.</summary>
        public void RequestTeam(Team team)
        {
            if (Phase != ClientPhase.Playing) return;
            _writer.Clear();
            new TeamSelectRequest { Team = team }.Write(_writer);
            _sender.Send(_writer, Delivery.ReliableOrdered);
        }

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
                    case MessageType.GameData:
                        OnGameData(GameDataFiles.Read(r));
                        break;
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

        /// <summary>Regeln des Servers übernehmen. Sie kommen vor der Begrüßung, damit Vorhersage und Anzeige gleich mit ihnen rechnen.</summary>
        private void OnGameData(GameDataFiles files)
        {
            if (World != null || HasServerRules) throw new ProtocolException("Spielregeln nach der Begrüßung.");
            try
            {
                Rules = GameRules.Parse(files, _playerRigJson);
            }
            catch (FormatException e)
            {
                Fail("Die Spielregeln des Servers sind ungültig: " + e.Message);
                return;
            }
            HasServerRules = true;
            Class = Rules.Classes.None;
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
            Zones = new MapZones(Metadata);
            Prediction = new ClientPrediction(World, Class, welcome.Spawn, Zones);
            Projectiles = new NetProjectiles(World, Combat, Movement.DeltaTime, Zones);
            Health = 0; // Neu Beigetretene schauen zu, bis sie ein Team gewählt haben (der Snapshot bestätigt es)
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

            // Die Klasse wechselt nur beim Respawn. Ab der bestätigten Eingabe gilt die aus dem Snapshot, auch für die neueren.
            Class = Classes.Get(s.OwnClass) ?? Classes.None;
            Prediction.Class = Class;
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
                    _pendingEdits.Remove(e.Seq);
                    _serverBlocks = e.Supply;
                    if (e.Result != BlockEditResult.Ok) EditRejected?.Invoke(e.Result);
                    break;
                case WorldEventType.BlockSupply:
                    _serverBlocks = e.Supply;
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
                        RespawnTicks = Combat.RespawnTicks;
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
                case WorldEventType.TeamChanged:
                    if (e.PlayerId == PlayerId)
                    {
                        Team = e.Team;
                        Prediction.Team = e.Team;
                    }
                    else if (_players.TryGetValue(e.PlayerId, out var changed)) changed.Team = e.Team;
                    MatchEvent?.Invoke(e);
                    break;
                case WorldEventType.PlayerStats:
                    if (e.PlayerId == PlayerId)
                    {
                        Kills = e.Kills;
                        Deaths = e.Deaths;
                    }
                    else if (_players.TryGetValue(e.PlayerId, out var stats))
                    {
                        stats.Kills = e.Kills;
                        stats.Deaths = e.Deaths;
                    }
                    MatchEvent?.Invoke(e);
                    break;
                case WorldEventType.MatchState:
                    if (e.Match.MatchNumber != Match.MatchNumber) StartNewMatch();
                    Match = e.Match;
                    MatchEvent?.Invoke(e);
                    break;
                case WorldEventType.TeamDenied:
                    MatchEvent?.Invoke(e);
                    break;
            }
        }

        /// <summary>
        /// Neustart: Geschosse, fallende Gruppen, Blockschaden und Kills/Tode vergessen. Die Blöcke setzt der Server mit
        /// Blockänderungen gleich danach auf den Stand der Map zurück.
        /// </summary>
        private void StartNewMatch()
        {
            Projectiles.Clear();
            Falling.Clear();
            _blockDamage.Clear();
            Kills = Deaths = 0;
            foreach (var p in _players.Values) p.Kills = p.Deaths = 0;
        }

        // ---- Senden ----

        /// <summary>
        /// Ein Simulationsschritt des eigenen Spielers: vorhersagen und die Eingabe an den Server schicken.
        /// Liefert den Platz der Waffe, die in diesem Tick feuert (0 = keine), damit die Anzeige Mündungsfeuer zeigen kann.
        /// Beim Feuern geht der gerade gezeigte Zeitpunkt (<see cref="RenderTick"/>) mit: Gegen ihn prüft der Server den Treffer.
        /// Tot und nach dem Matchende zählen nur Blickrichtung und Waffenwahl (wie auf dem Server).
        /// </summary>
        public int Tick(PlayerInput input)
        {
            if (Phase != ClientPhase.Playing) return 0;
            if (!Alive || MatchOver) input.Buttons = InputButtons.None;
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
            if (MatchOver) return BlockEditResult.MatchOver;
            var result = BlockEditing.CanRemove(World, Registry, x, y, z, distance, Zones);
            if (result == BlockEditResult.Ok) SendEdit(BlockEditKind.Remove, x, y, z, BlockRegistry.Air);
            return result;
        }

        public BlockEditResult RequestPlace(int x, int y, int z, byte id, float distance)
        {
            if (Phase != ClientPhase.Playing) return BlockEditResult.OutOfReach;
            if (!Alive) return BlockEditResult.Dead;
            if (MatchOver) return BlockEditResult.MatchOver;
            var result = BlockEditing.CanPlace(World, Registry, x, y, z, id, distance, Prediction.State, Movement, Zones);
            if (result == BlockEditResult.Ok && BlocksLeft <= 0) result = BlockEditResult.NoBlocks;
            if (result == BlockEditResult.Ok) SendEdit(BlockEditKind.Place, x, y, z, id);
            return result;
        }

        private void SendEdit(BlockEditKind kind, int x, int y, int z, byte id)
        {
            var seq = ++_editSeq;
            Blocks.Predict(seq, x, y, z, id);
            _pendingEdits[seq] = kind;
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
