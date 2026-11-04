using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;

namespace AoS2.Server;

/// <summary>
/// Ein Spieler aus Sicht des Servers. Der Server rechnet seine Bewegung mit denselben Eingaben wie der Client
/// (gleicher Code aus AoS2.Shared), Eingabe für Eingabe in der Reihenfolge ihrer Tick-Nummern.
/// </summary>
public sealed class ServerPlayer
{
    /// <summary>So viele verarbeitete Ticks merkt sich der Server (Zustand + Eingabe), um Blockaktionen nachträglich zu prüfen.</summary>
    internal const int HistorySize = 64;

    /// <summary>So viele Server-Ticks (gut 1 s) reicht der Verlauf für den Lag-Ausgleich zurück.</summary>
    internal const int ViewHistorySize = 80;

    private readonly PlayerState[] _historyStates = new PlayerState[HistorySize];
    private readonly PlayerInput[] _historyInputs = new PlayerInput[HistorySize];
    private readonly PlayerSnapshot[] _views = new PlayerSnapshot[ViewHistorySize];
    private readonly uint[] _viewTicks = new uint[ViewHistorySize];
    private readonly bool[] _viewValid = new bool[ViewHistorySize];

    internal ServerPlayer(byte id, Guid accountId, string name, PlayerState spawn)
    {
        Id = id;
        AccountId = accountId;
        Name = name;
        State = spawn;
        _historyStates[0] = spawn;
    }

    public byte Id { get; }
    public Guid AccountId { get; }
    public string Name { get; }

    public PlayerState State { get; internal set; }

    /// <summary>Zuletzt verarbeitete Eingabe (Blickrichtung für Snapshots).</summary>
    public PlayerInput LastInput { get; internal set; }

    /// <summary>Höchster Eingabe-Tick, der angekommen ist. Immer <c>LastProcessedTick + Inputs.Count</c>.</summary>
    public uint LastReceivedTick { get; internal set; }

    /// <summary>Höchster Eingabe-Tick, den der Server gerechnet (oder verworfen) hat. Steht im Snapshot an den Spieler.</summary>
    public uint LastProcessedTick { get; internal set; }

    /// <summary>Angekommene, noch nicht gerechnete Eingaben; die erste gehört zu Tick <c>LastProcessedTick + 1</c>.</summary>
    internal readonly Queue<PlayerInput> Inputs = new();

    /// <summary>Wie viele Eingaben der Spieler noch rechnen lassen darf (gegen zu schnelles Senden).</summary>
    internal int Budget;

    internal readonly List<PendingEdit> Edits = new();

    /// <summary>Eingabe-Tick der letzten angenommenen Blockaktion (für die Pause dazwischen).</summary>
    internal uint? LastEditTick;

    /// <summary>Eingaben, die verworfen wurden (zu viele auf einmal); für Tests und Statistik.</summary>
    public int DroppedInputs { get; internal set; }

    // ---- Kampf ----

    /// <summary>Lebenspunkte (0 = tot).</summary>
    public int Health { get; internal set; }

    public bool Alive => Health > 0;

    /// <summary>Tot: Server-Tick, zu dem der Spieler wieder erscheint.</summary>
    public uint RespawnAt { get; internal set; }

    /// <summary>Zählt die Leben (+1 bei jedem Respawn).</summary>
    public byte Life { get; internal set; }

    /// <summary>Team (<see cref="Team.None"/>: Zuschauer, hat noch keins gewählt).</summary>
    public Team Team { get; internal set; }

    /// <summary>Kills von Gegnern im laufenden Match.</summary>
    public int Kills { get; internal set; }
    public int Deaths { get; internal set; }

    // ---- Klasse ----

    /// <summary>Aktive Klasse: Waffen, Tempo, Kollisionshöhe, Ausrüstung (Trefferzonen). Ändert sich nur beim Respawn.</summary>
    public PlayerClass Class { get; internal set; } = null!;

    /// <summary>Gewählte Klasse für den nächsten Spawn (<c>null</c> = keine gewählt, dann <c>defaultClass</c>).</summary>
    public PlayerClass? NextClass { get; internal set; }

    /// <summary>Blockvorrat: Bauen −1, Abbauen +1 (höchstens <see cref="PlayerClass.Blocks"/>), der Respawn füllt auf.</summary>
    public int Blocks { get; internal set; }

    /// <summary>Waffen und Munition, Tick für Tick aus den Eingaben gerechnet wie auf dem Client.</summary>
    internal WeaponState WeaponsState;

    public WeaponState Weapons => WeaponsState;

    internal AnimationState AnimationState;

    public AnimationState Animation => AnimationState;

    internal void Remember(uint tick, PlayerState state, PlayerInput input)
    {
        _historyStates[tick % HistorySize] = state;
        _historyInputs[tick % HistorySize] = input;
    }

    /// <summary>Zustand und Eingabe nach Tick <paramref name="tick"/>, solange er im Verlauf steht.</summary>
    internal bool TryRecall(uint tick, out PlayerState state, out PlayerInput input)
    {
        state = default;
        input = default;
        if (tick > LastProcessedTick || LastProcessedTick - tick >= HistorySize) return false;
        state = _historyStates[tick % HistorySize];
        input = _historyInputs[tick % HistorySize];
        return true;
    }

    /// <summary>So, wie andere Clients den Spieler am Ende von Server-Tick <paramref name="tick"/> sehen (für den Lag-Ausgleich).</summary>
    internal void RecordView(uint tick, PlayerSnapshot view)
    {
        var i = tick % ViewHistorySize;
        _views[i] = view.Quantized();
        _viewTicks[i] = tick;
        _viewValid[i] = true;
    }

    internal bool TryGetView(uint tick, out PlayerSnapshot view)
    {
        var i = tick % ViewHistorySize;
        view = _views[i];
        return _viewValid[i] && _viewTicks[i] == tick;
    }
}

internal readonly record struct PendingEdit(BlockEditRequest Request, uint ReceivedAt);
