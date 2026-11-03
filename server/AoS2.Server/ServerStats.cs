using AoS2.Shared.Net;

namespace AoS2.Server;

/// <summary>
/// Messwerte des laufenden Servers: wie lange ein Tick rechnet und was er verschickt. Die Hauptschleife und
/// <see cref="GameServer"/> füllen sie, der Server schreibt sie regelmäßig ins Log, der Lasttest wertet sie aus.
/// Nur aus dem Thread der Hauptschleife schreiben; Lesen aus einem anderen Thread liefert höchstens leicht veraltete Werte.
/// </summary>
public sealed class ServerStats
{
    private readonly List<double>? _samples;

    /// <param name="keepSamples">Jede Tick-Dauer aufheben (für Perzentile im Lasttest).</param>
    public ServerStats(bool keepSamples = false)
    {
        if (keepSamples) _samples = new List<double>();
    }

    public long Ticks { get; private set; }
    public double TotalStepMs { get; private set; }
    public double MaxStepMs { get; private set; }

    /// <summary>Ticks, die länger als ein ganzer Tick (1/60 s) gerechnet haben.</summary>
    public long SlowTicks { get; private set; }

    public double AverageStepMs => Ticks == 0 ? 0 : TotalStepMs / Ticks;

    public long Snapshots { get; private set; }
    public long SnapshotBytes { get; private set; }
    public int MaxSnapshotBytes { get; private set; }
    public long EventMessages { get; private set; }
    public long EventBytes { get; private set; }

    /// <summary>Nutzdaten aller Nachrichten an Clients (ohne UDP- und LiteNetLib-Kopfdaten).</summary>
    public long SentBytes => SnapshotBytes + EventBytes;

    public double AverageSnapshotBytes => Snapshots == 0 ? 0 : (double)SnapshotBytes / Snapshots;

    public void RecordStep(double milliseconds, double tickMilliseconds)
    {
        Ticks++;
        TotalStepMs += milliseconds;
        if (milliseconds > MaxStepMs) MaxStepMs = milliseconds;
        if (milliseconds > tickMilliseconds) SlowTicks++;
        _samples?.Add(milliseconds);
    }

    public void RecordSend(int bytes, Delivery delivery)
    {
        if (delivery == Delivery.Sequenced)
        {
            Snapshots++;
            SnapshotBytes += bytes;
            if (bytes > MaxSnapshotBytes) MaxSnapshotBytes = bytes;
        }
        else
        {
            EventMessages++;
            EventBytes += bytes;
        }
    }

    /// <summary>Tick-Dauer, die <paramref name="fraction"/> aller Ticks nicht überschreiten (z. B. 0,99). Nur mit aufgehobenen Werten.</summary>
    public double StepPercentile(double fraction)
    {
        if (_samples == null || _samples.Count == 0) return 0;
        var sorted = _samples.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Min(sorted.Length - 1, (int)(fraction * sorted.Length))];
    }

    public void Reset()
    {
        Ticks = SlowTicks = Snapshots = SnapshotBytes = EventMessages = EventBytes = 0;
        TotalStepMs = MaxStepMs = 0;
        MaxSnapshotBytes = 0;
        _samples?.Clear();
    }
}
