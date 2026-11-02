using System.Diagnostics;

namespace AoS2.Launcher.Services;

public interface IGameStarter
{
    /// <param name="environment">Zusätzliche Umgebungsvariablen nur für den Spielprozess (Anmeldung).</param>
    void Start(string executablePath, IReadOnlyDictionary<string, string> environment);
}

public sealed class ProcessGameStarter : IGameStarter
{
    public void Start(string executablePath, IReadOnlyDictionary<string, string> environment)
    {
        // Unity sucht seinen Datenordner (AoS2_Data) neben der exe, deshalb dort starten.
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
        };

        // Das Sitzungstoken geht als Umgebungsvariable an das Spiel, nicht als Kommandozeilenargument:
        // Die Kommandozeile zeigen Task-Manager, Prozesslisten und Absturzberichte an, die Umgebung nicht.
        // Die Variable gilt nur für diesen einen Prozess, und das Spiel entfernt sie nach dem Lesen.
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        using var _ = Process.Start(start);
    }
}
