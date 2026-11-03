using AoS2.LoadTest;
using Xunit.Abstractions;

namespace AoS2.Server.Tests;

/// <summary>
/// Lasttest im Kleinen: 32 Bots (16 gegen 16) über echtes UDP gegen einen Server mit seiner echten Hauptschleife, alles in
/// diesem Prozess. Die Grenzen sind großzügig (Testrechner sind unterschiedlich schnell); die Messwerte stehen in der
/// Testausgabe. Ausführlicher: <c>dotnet run -c Release --project tools/AoS2.LoadTest</c>.
/// </summary>
[Collection("UDP")]
public sealed class LoadTests(ITestOutputHelper output)
{
    [Fact]
    public void ThirtyTwoBots_ServerKeepsSixtyTicks_SnapshotsFitInOnePacket()
    {
        var report = LoadTestRun.Run(new LoadTestOptions
        {
            Bots = 32,
            Seconds = 8,
            WarmupSeconds = 2,
            DataFolder = TestArena.RepoFile("data"),
            MapsFolder = TestArena.RepoFile("maps"),
            ScoreLimit = 10, // das Match endet unter Last und beginnt neu
            MatchEndSeconds = 2,
        }, output.WriteLine);
        output.WriteLine(report.ToString());

        Assert.Equal(32, report.Joined);
        Assert.Equal((16, 16), (report.Blue, report.Green));
        Assert.Equal(0, report.Disconnected);
        Assert.InRange(report.TicksPerSecond, 55, 62);
        Assert.True(report.P99StepMs < 1000.0 / 60, $"99 % der Ticks unter einem Tick: {report.P99StepMs:0.00} ms");
        Assert.InRange(report.MaxSnapshotBytes, 1, Match.MaxSnapshotBytes);
        Assert.InRange(report.SnapshotsPerClientPerSecond, 25, 31);
        Assert.InRange(report.ClientSnapshotsPerSecond, 25, 31);
        Assert.True(report.Shots > 0, "die Bots kämpfen");
    }
}
