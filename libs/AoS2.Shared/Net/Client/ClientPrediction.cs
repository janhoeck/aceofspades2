using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net.Client
{
    /// <summary>
    /// Vorhersage und Abgleich für den eigenen Spieler.
    /// <list type="number">
    /// <item><b>Vorhersage (Prediction):</b> Jeder Tick wird sofort mit dem gemeinsamen Bewegungscode gerechnet,
    ///       ohne auf den Server zu warten. Eingabe und Ergebnis kommen in einen Verlauf.</item>
    /// <item><b>Abgleich (Reconciliation):</b> Ein Snapshot sagt: „Nach deiner Eingabe Nummer k stehst du hier.“
    ///       Dann wird der eigene Zustand auf den des Servers gesetzt und alle Eingaben nach k werden noch einmal
    ///       gerechnet. Stimmt alles, kommt genau dieselbe Position heraus (der Code ist deterministisch);
    ///       hat sich etwas geändert (z. B. hat ein anderer Spieler einen Block gebaut), korrigiert sich der Client so.</item>
    /// </list>
    /// Die Waffen (Munition, Feuerpause, Nachladen, <see cref="WeaponSim"/>) laufen genauso mit: Der Client zeigt Schüsse
    /// sofort, der Server rechnet dieselben Eingaben und bestätigt oder korrigiert.
    /// </summary>
    public sealed class ClientPrediction
    {
        /// <summary>So viele Ticks (gut 4 s) kann der Client dem Server voraus sein, ohne dass der Verlauf überläuft.</summary>
        public const int HistorySize = 256;

        private readonly PlayerInput[] _inputs = new PlayerInput[HistorySize];
        private readonly PlayerState[] _states = new PlayerState[HistorySize];
        private readonly WeaponState[] _weapons = new WeaponState[HistorySize];
        private readonly VoxelWorld _world;
        private readonly MapZones _zones;

        /// <summary>Nur Bewegung, ohne Waffen.</summary>
        public ClientPrediction(VoxelWorld world, MovementSettings settings, PlayerState start, MapZones zones = null)
            : this(world, new PlayerClass("none", settings, null, null), start, zones)
        {
        }

        /// <param name="cls">Klasse des Spielers: Bewegung (Tempo, Kollisionshöhe) und Waffen.</param>
        /// <param name="zones">Zonen der Map (unsichtbare Wände, kein Feuern im Spawn); <c>null</c> = keine.</param>
        public ClientPrediction(VoxelWorld world, PlayerClass cls, PlayerState start, MapZones zones = null)
        {
            _world = world;
            Class = cls;
            _zones = zones ?? MapZones.None;
            State = start;
            _states[0] = start;
            Weapons = _weapons[0] = WeaponState.Full(cls);
        }

        /// <summary>
        /// Klasse, mit der gerechnet wird. Sie ändert sich nur beim Respawn; der Snapshot sagt, welche nach der bestätigten
        /// Eingabe gilt (<see cref="Snapshot.OwnClass"/>), und die Session setzt sie vor dem Abgleich.
        /// </summary>
        public PlayerClass Class { get; set; }

        /// <summary>Eigenes Team: Die Spawnzone des Gegners ist eine Wand (wie auf dem Server).</summary>
        public Team Team { get; set; }

        /// <summary>Vorhergesagter Zustand nach der Eingabe <see cref="Tick"/>.</summary>
        public PlayerState State { get; private set; }

        /// <summary>Vorhergesagte Waffen nach der Eingabe <see cref="Tick"/>.</summary>
        public WeaponState Weapons { get; private set; }

        /// <summary>Nummer der letzten gerechneten Eingabe (die erste ist 1).</summary>
        public uint Tick { get; private set; }

        /// <summary>Letzte Eingabe, die der Server bestätigt hat.</summary>
        public uint AckedTick { get; private set; }

        /// <summary>Wie oft der Server etwas anderes errechnet hat als die Vorhersage.</summary>
        public int Mispredictions { get; private set; }

        public uint UnackedCount => Tick - AckedTick;

        public PlayerState Step(PlayerInput input) => Step(input, out _);

        /// <summary>Ein Tick: Bewegung, dann Waffen. <paramref name="firedSlot"/> = Platz der Waffe, die jetzt feuert (sonst 0).</summary>
        public PlayerState Step(PlayerInput input, out int firedSlot)
        {
            Tick++;
            var weapons = Weapons;
            State = Simulate(State, ref weapons, input, out firedSlot);
            Weapons = weapons;
            _inputs[Tick % HistorySize] = input;
            _states[Tick % HistorySize] = State;
            _weapons[Tick % HistorySize] = Weapons;
            return State;
        }

        private PlayerState Simulate(PlayerState state, ref WeaponState weapons, PlayerInput input, out int firedSlot)
        {
            state = PlayerMovement.Step(state, input, _world, Class.Movement, _zones.BarriersFor(Team));
            firedSlot = WeaponSim.Step(ref weapons, input, Class, _zones.InSpawnZone(state.Position));
            return state;
        }

        /// <summary>Eingabe eines Ticks, solange er noch im Verlauf steht.</summary>
        public bool TryGetInput(uint tick, out PlayerInput input)
        {
            input = default;
            if (tick == 0 || tick > Tick || Tick - tick >= HistorySize) return false;
            input = _inputs[tick % HistorySize];
            return true;
        }

        public Vec3 Reconcile(uint ackTick, PlayerState server) => Reconcile(ackTick, server, Weapons);

        /// <summary>
        /// Übernimmt den Zustand des Servers nach Eingabe <paramref name="ackTick"/> und rechnet die neueren Eingaben neu.
        /// Liefert, um wie viel sich die aktuelle Position dadurch verschoben hat (für eine weiche Korrektur der Anzeige).
        /// </summary>
        public Vec3 Reconcile(uint ackTick, PlayerState server, WeaponState serverWeapons)
        {
            if (ackTick < AckedTick || ackTick > Tick) return Vec3.Zero; // veraltet oder unmöglich
            var before = State;

            if (Tick - ackTick >= HistorySize)
            {
                // So weit zurück reicht der Verlauf nicht: einfach den Server-Zustand übernehmen.
                AckedTick = ackTick;
                State = server;
                Weapons = serverWeapons;
                _states[Tick % HistorySize] = server;
                _weapons[Tick % HistorySize] = serverWeapons;
                Mispredictions++;
                return State.Position - before.Position;
            }

            if (ackTick > AckedTick && (!SameState(_states[ackTick % HistorySize], server) || !_weapons[ackTick % HistorySize].Equals(serverWeapons)))
                Mispredictions++;
            AckedTick = ackTick;

            var s = server;
            var w = serverWeapons;
            _states[ackTick % HistorySize] = s;
            _weapons[ackTick % HistorySize] = w;
            for (var t = ackTick + 1; t <= Tick; t++)
            {
                s = Simulate(s, ref w, _inputs[t % HistorySize], out _);
                _states[t % HistorySize] = s;
                _weapons[t % HistorySize] = w;
            }
            State = s;
            Weapons = w;
            return State.Position - before.Position;
        }

        /// <summary>Schreibt die noch nicht bestätigten Eingaben (höchstens <see cref="NetLimits.MaxInputsPerPacket"/>) in ein Paket.</summary>
        public void FillInputPacket(InputPacket packet)
        {
            packet.Inputs.Clear();
            packet.NewestTick = Tick;
            if (Tick == 0) return;
            var count = UnackedCount;
            if (count < 1) count = 1;
            if (count > NetLimits.MaxInputsPerPacket) count = NetLimits.MaxInputsPerPacket;
            for (var t = Tick - count + 1; t <= Tick; t++) packet.Inputs.Add(_inputs[t % HistorySize]);
        }

        public static bool SameState(PlayerState a, PlayerState b) =>
            a.Position.Equals(b.Position) && a.Velocity.Equals(b.Velocity) && a.OnGround == b.OnGround && a.Crouching == b.Crouching;
    }
}
