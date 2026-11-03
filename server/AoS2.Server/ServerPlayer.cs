using AoS2.Shared.Movement;
using AoS2.Shared.Net;

namespace AoS2.Server;

/// <summary>
/// Ein Spieler aus Sicht des Servers. Der Server rechnet seine Bewegung mit denselben Eingaben wie der Client
/// (gleicher Code aus AoS2.Shared), Eingabe für Eingabe in der Reihenfolge ihrer Tick-Nummern.
/// </summary>
public sealed class ServerPlayer
{
    /// <summary>So viele verarbeitete Ticks merkt sich der Server (Zustand + Eingabe), um Blockaktionen nachträglich zu prüfen.</summary>
    internal const int HistorySize = 64;

    private readonly PlayerState[] _historyStates = new PlayerState[HistorySize];
    private readonly PlayerInput[] _historyInputs = new PlayerInput[HistorySize];

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
}

internal readonly record struct PendingEdit(BlockEditRequest Request, uint ReceivedAt);
