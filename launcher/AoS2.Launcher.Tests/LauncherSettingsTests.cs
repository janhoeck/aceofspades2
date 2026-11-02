using AoS2.Launcher.Services;
using AoS2.Updates.Tests;

namespace AoS2.Launcher.Tests;

public sealed class LauncherSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var settings = new LauncherSettingsStore(_temp.Combine("launcher.json")).Load();

        Assert.Null(settings.GameDirectory);
        Assert.True(settings.CloseOnGameStart);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new LauncherSettingsStore(_temp.Combine("sub", "launcher.json"));

        store.Save(new LauncherSettings { GameDirectory = @"D:\Spiele\Ace of Spades 2", CloseOnGameStart = false });

        Assert.Equal(new LauncherSettings { GameDirectory = @"D:\Spiele\Ace of Spades 2", CloseOnGameStart = false }, store.Load());
    }

    [Fact]
    public void Load_BrokenFile_ReturnsDefaults()
    {
        var path = _temp.Combine("launcher.json");
        File.WriteAllText(path, "{ kaputt");

        Assert.Equal(new LauncherSettings(), new LauncherSettingsStore(path).Load());
    }
}
