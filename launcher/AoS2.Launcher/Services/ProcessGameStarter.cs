using System.Diagnostics;

namespace AoS2.Launcher.Services;

public interface IGameStarter
{
    void Start(string executablePath);
}

public sealed class ProcessGameStarter : IGameStarter
{
    public void Start(string executablePath)
    {
        // Unity sucht seinen Datenordner (AoS2_Data) neben der exe, deshalb dort starten.
        using var _ = Process.Start(new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
        });
    }
}
