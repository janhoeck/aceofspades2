using AoS2.Shared;

namespace AoS2.Balance.Tests;

public class BalanceReportTests
{
    private static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AoS2.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, relativePath);
    }

    private static GameRules RealRules() =>
        GameRules.Parse(GameDataFiles.Load(name => File.ReadAllText(RepoFile("data/" + name))), File.ReadAllText(RepoFile("data/player_rig.json")));

    private static string Report() => BalanceReport.Build(RealRules(), BalanceReport.DefaultDistances);

    [Fact]
    public void Report_ListsClassesWeaponsAndExplosives()
    {
        var md = Report();
        Assert.Contains("| Assault (Standard) | Sturmgewehr, Pistole, Granate | 50 | 0,2 s |", md);
        Assert.Contains("| Miner | MP, Granate | 100 | 0,1 s |", md);
        Assert.Contains("| Recon | Scharfschützengewehr, Pistole | 30 | 0,2 s | 4,95 / 7,15 Blöcke/s |", md);
        Assert.Contains("| Sturmgewehr | Assault | 25 | 10 | 250 | 30 + 120 | 2 s | ab 40, 60 % ab 100 | 200 |", md);
        Assert.Contains("| Scharfschützengewehr | Recon | 70 | 0,83 (Einzelfeuer) |", md);
        Assert.Contains("### MP", md);
        Assert.Contains("| Raketenwerfer | Rocketeer | 1 + 5 | 120 | 90 | 60 | 30 | 0,67 Blöcke | 4 | 3 / 260 |", md);
    }

    [Fact]
    public void Report_HitsAndTimeToKill_PerZoneAndDistance()
    {
        var md = Report();
        // Sturmgewehr: bis 40 Blöcke 4 Rumpftreffer in 0,3 s, auf 100 Blöcke 15 Schaden je Treffer = 7 Treffer.
        var rifle = md[md.IndexOf("### Sturmgewehr", StringComparison.Ordinal)..];
        rifle = rifle[..rifle.IndexOf("###", 3, StringComparison.Ordinal)];
        Assert.Contains("| Zone | 0 | 25 | 50 | 100 | 200 |", rifle);
        Assert.Contains("| Kopf | 2 (0,10 s) | 2 (0,10 s) | 3 (0,20 s) | 4 (0,30 s) | 4 (0,30 s) |", rifle); // auf 50: 23,3 · 2 = 47
        Assert.Contains("| Rumpf | 4 (0,30 s) | 4 (0,30 s) | 5 (0,40 s) | 7 (0,60 s) | 7 (0,60 s) |", rifle);
        // Scharfschütze: Kopfschuss tötet auf jede Entfernung; außerhalb der Reichweite ein Strich (Pistole: 120).
        Assert.Contains("| Kopf | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) | 1 (0,00 s) |", md);
        var pistol = md[md.IndexOf("### Pistole", StringComparison.Ordinal)..];
        Assert.Contains("| – |", pistol[..pistol.IndexOf("## Sprengstoff", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Report_OwnDistances_AndChangedDataShowsUp()
    {
        var files = GameDataFiles.Load(name => File.ReadAllText(RepoFile("data/" + name)));
        files.Weapons = files.Weapons.Replace("\"damage\": 25,", "\"damage\": 34,"); // Sturmgewehr stärker
        var rules = GameRules.Parse(files, File.ReadAllText(RepoFile("data/player_rig.json")));
        var md = BalanceReport.Build(rules, [10]);
        Assert.Contains("| Zone | 10 |", md);
        Assert.Contains("| Rumpf | 3 (0,20 s) |", md);
    }
}
