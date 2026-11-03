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
    /// </summary>
    public sealed class ClientPrediction
    {
        /// <summary>So viele Ticks (gut 4 s) kann der Client dem Server voraus sein, ohne dass der Verlauf überläuft.</summary>
        public const int HistorySize = 256;

        private readonly PlayerInput[] _inputs = new PlayerInput[HistorySize];
        private readonly PlayerState[] _states = new PlayerState[HistorySize];
        private readonly VoxelWorld _world;
        private readonly MovementSettings _settings;

        public ClientPrediction(VoxelWorld world, MovementSettings settings, PlayerState start)
        {
            _world = world;
            _settings = settings;
            State = start;
            _states[0] = start;
        }

        /// <summary>Vorhergesagter Zustand nach der Eingabe <see cref="Tick"/>.</summary>
        public PlayerState State { get; private set; }

        /// <summary>Nummer der letzten gerechneten Eingabe (die erste ist 1).</summary>
        public uint Tick { get; private set; }

        /// <summary>Letzte Eingabe, die der Server bestätigt hat.</summary>
        public uint AckedTick { get; private set; }

        /// <summary>Wie oft der Server etwas anderes errechnet hat als die Vorhersage.</summary>
        public int Mispredictions { get; private set; }

        public uint UnackedCount => Tick - AckedTick;

        public PlayerState Step(PlayerInput input)
        {
            Tick++;
            State = PlayerMovement.Step(State, input, _world, _settings);
            _inputs[Tick % HistorySize] = input;
            _states[Tick % HistorySize] = State;
            return State;
        }

        /// <summary>Eingabe eines Ticks, solange er noch im Verlauf steht.</summary>
        public bool TryGetInput(uint tick, out PlayerInput input)
        {
            input = default;
            if (tick == 0 || tick > Tick || Tick - tick >= HistorySize) return false;
            input = _inputs[tick % HistorySize];
            return true;
        }

        /// <summary>
        /// Übernimmt den Zustand des Servers nach Eingabe <paramref name="ackTick"/> und rechnet die neueren Eingaben neu.
        /// Liefert, um wie viel sich die aktuelle Position dadurch verschoben hat (für eine weiche Korrektur der Anzeige).
        /// </summary>
        public Vec3 Reconcile(uint ackTick, PlayerState server)
        {
            if (ackTick < AckedTick || ackTick > Tick) return Vec3.Zero; // veraltet oder unmöglich
            var before = State;

            if (Tick - ackTick >= HistorySize)
            {
                // So weit zurück reicht der Verlauf nicht: einfach den Server-Zustand übernehmen.
                AckedTick = ackTick;
                State = server;
                _states[Tick % HistorySize] = server;
                Mispredictions++;
                return State.Position - before.Position;
            }

            if (ackTick > AckedTick && !SameState(_states[ackTick % HistorySize], server)) Mispredictions++;
            AckedTick = ackTick;

            var s = server;
            _states[ackTick % HistorySize] = s;
            for (var t = ackTick + 1; t <= Tick; t++)
            {
                s = PlayerMovement.Step(s, _inputs[t % HistorySize], _world, _settings);
                _states[t % HistorySize] = s;
            }
            State = s;
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
