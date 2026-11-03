using System.Globalization;
using AoS2.LoadTest;
using AoS2.Shared.Net;

// Lasttest: dotnet run -c Release --project tools/AoS2.LoadTest -- [--bots 32] [--seconds 30] [--server host:port]
// Ohne --server läuft ein Server im selben Prozess (dann gibt es auch Tick- und Bandbreitenwerte).
// Mit --server spielen die Bots auf einem laufenden Server, der mit --dev gestartet sein muss.

Console.OutputEncoding = System.Text.Encoding.UTF8;
var bots = 32;
var seconds = 30.0;
ServerAddress? server = null;
for (var i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Nach {args[i]} fehlt ein Wert.");
    switch (args[i])
    {
        case "--bots": bots = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--seconds": seconds = double.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--server":
            if (!ServerAddress.TryParse(Value(), out var address)) throw new ArgumentException("--server erwartet host:port");
            server = address;
            break;
        case "--help":
        case "-h":
            Console.WriteLine("AoS2.LoadTest [--bots 32] [--seconds 30] [--server host:port]");
            return 0;
        default:
            Console.Error.WriteLine($"Unbekannte Option {args[i]}");
            return 2;
    }
}

Console.WriteLine($"Lasttest: {bots} Bots, {seconds:0} s, {(server == null ? "Server im selben Prozess" : "Server " + server)}");
var report = LoadTestRun.Run(new LoadTestOptions { Bots = bots, Seconds = seconds, Server = server }, Console.WriteLine);
Console.WriteLine();
Console.WriteLine(report);
return report.Joined == bots && report.Disconnected == 0 ? 0 : 1;
