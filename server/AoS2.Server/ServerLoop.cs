using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AoS2.Server;

/// <summary>
/// Hauptschleife: Netzwerk etwa jede Millisekunde abfragen, die Simulation genau <c>TickRate</c> Mal pro Sekunde
/// weiterrechnen. Die Ticks richten sich nach einer Stoppuhr, nicht nach der Schlafdauer: Schläft der Thread
/// länger als geplant, werden die fehlenden Ticks direkt nachgeholt, und im Mittel stimmt die Rate genau.
/// Jeder Tick wird gemessen (<see cref="GameServer.Stats"/>).
/// </summary>
public static class ServerLoop
{
    /// <summary>Nach einem Hänger (z. B. Debugger) höchstens so viele Ticks nachholen, dann die Uhr neu stellen.</summary>
    private const int MaxCatchUpTicks = 30;

    /// <param name="report">Wird alle <paramref name="reportSeconds"/> Sekunden im selben Thread aufgerufen (z. B. für das Log).</param>
    public static void Run(GameServer server, CancellationToken cancellationToken, Action? report = null, double reportSeconds = 60)
    {
        var tickSeconds = 1.0 / server.Match.Content.Movement.TickRate;
        var tickMs = tickSeconds * 1000;
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        var nextReport = reportSeconds;

        // Windows schläft sonst in 15,6-ms-Schritten; so wird Thread.Sleep(1) wirklich etwa 1 ms.
        var finerTimer = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                server.Update();
                var ticks = 0;
                while (clock.Elapsed.TotalSeconds >= next && ticks < MaxCatchUpTicks)
                {
                    var start = clock.Elapsed.TotalMilliseconds;
                    server.Match.Step();
                    server.Stats.RecordStep(clock.Elapsed.TotalMilliseconds - start, tickMs);
                    next += tickSeconds;
                    ticks++;
                }
                if (ticks == MaxCatchUpTicks) next = clock.Elapsed.TotalSeconds;
                if (report != null && clock.Elapsed.TotalSeconds >= nextReport)
                {
                    report();
                    nextReport += reportSeconds;
                }
                Thread.Sleep(1);
            }
        }
        finally
        {
            if (finerTimer) TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
