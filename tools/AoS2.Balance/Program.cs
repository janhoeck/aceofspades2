using System.Globalization;
using AoS2.Balance;
using AoS2.Shared;

const string usage = """
    Balancing-Tabelle aus den Spieldaten: Klassen, Waffen, Treffer und Zeit bis zum Tod je Zone und Entfernung, Sprengstoff.

    Aufruf: dotnet run --project tools/AoS2.Balance -- [--data <Ordner>] [--out <Datei>] [--distances 0,25,50,100]

      --data       Ordner mit movement.json, combat.json, weapons.json, classes.json, player_rig.json
                   (Standard: data/ im Repository)
      --out        Markdown-Datei schreiben (z. B. docs/balancing.md), sonst Ausgabe auf der Konsole
      --distances  Entfernungen in Blöcken für die Tabelle „Treffer bis zum Tod“
    """;

string? data = null, output = null;
IReadOnlyList<float> distances = BalanceReport.DefaultDistances;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Nach {args[i]} fehlt ein Wert.");
    try
    {
        switch (args[i])
        {
            case "--data": data = Next(); break;
            case "--out": output = Next(); break;
            case "--distances":
                distances = Next().Split(',').Select(d => float.Parse(d.Trim(), CultureInfo.InvariantCulture)).ToList();
                if (distances.Count == 0 || distances.Any(d => d < 0)) throw new ArgumentException("Entfernungen müssen ≥ 0 sein.");
                break;
            case "-h" or "--help":
                Console.WriteLine(usage);
                return 0;
            default: throw new ArgumentException($"Unbekannte Option {args[i]}.");
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException)
    {
        Console.Error.WriteLine($"Fehler: {e.Message}{Environment.NewLine}");
        Console.WriteLine(usage);
        return 2;
    }
}

data ??= FindDataFolder();
try
{
    string Read(string name) => File.ReadAllText(Path.Combine(data, name));
    var rules = GameRules.Parse(GameDataFiles.Load(Read), Read("player_rig.json"));
    var report = BalanceReport.Build(rules, distances);
    if (output == null)
    {
        Console.Write(report);
    }
    else
    {
        File.WriteAllText(output, report.Replace("\r\n", "\n"));
        Console.WriteLine($"Geschrieben: {Path.GetFullPath(output)}");
    }
    return 0;
}
catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Fehler: {e.Message}");
    return 1;
}

// data/ neben AoS2.slnx (vom aktuellen Ordner aufwärts gesucht), sonst neben der exe.
static string FindDataFolder()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AoS2.slnx"))) return Path.Combine(dir.FullName, "data");
    return Path.Combine(AppContext.BaseDirectory, "data");
}
