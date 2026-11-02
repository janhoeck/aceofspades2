using AoS2.Launcher.Services;

namespace AoS2.Launcher.Tests;

public class LauncherOptionsTests
{
    [Fact]
    public void Create_OverrideWins()
    {
        var options = LauncherOptions.Create("https://test.example.com/", "https://built-in.example.com", null);

        Assert.Equal("https://test.example.com", options.UpdateLocation);
        Assert.Equal("https://test.example.com/launcher", options.LauncherUpdateLocation);
    }

    [Fact]
    public void Create_FallsBackToBuiltInUrl()
    {
        var options = LauncherOptions.Create("", "https://updates.example.com", null);

        Assert.Equal("https://updates.example.com", options.UpdateLocation);
        Assert.Equal(LauncherOptions.DefaultDataDirectory, options.DataDirectory);
    }

    [Fact]
    public void Create_NothingConfigured_HasNoUpdateLocation()
    {
        var options = LauncherOptions.Create(null, "", null);

        Assert.Null(options.UpdateLocation);
        Assert.Null(options.LauncherUpdateLocation);
    }

    [Fact]
    public void Create_FolderLocation_CombinesPath()
    {
        var options = LauncherOptions.Create(@"C:\aos2\release\", null, @"C:\Test\Daten");

        Assert.Equal(@"C:\aos2\release", options.UpdateLocation);
        Assert.Equal(Path.Combine(@"C:\aos2\release", "launcher"), options.LauncherUpdateLocation);
        Assert.Equal(Path.Combine(Path.GetFullPath(@"C:\Test\Daten"), "Game"), options.DefaultGameDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(@"C:\Test\Daten"), "launcher.json"), options.SettingsPath);
    }

    [Fact]
    public void Create_BackendUrl_OverrideWins_WithoutTrailingSlash()
    {
        Assert.Equal("http://localhost:5080", LauncherOptions.Create(null, null, null, "http://localhost:5080/", "https://api.example.com").BackendUrl);
        Assert.Equal("https://api.example.com", LauncherOptions.Create(null, null, null, " ", "https://api.example.com").BackendUrl);
        Assert.Null(LauncherOptions.Create(null, null, null, null, "").BackendUrl);
    }

    [Fact]
    public void SessionPath_IsInDataDirectory()
    {
        var options = LauncherOptions.Create(null, null, @"C:\Test\Daten");

        Assert.Equal(Path.Combine(Path.GetFullPath(@"C:\Test\Daten"), "session.dat"), options.SessionPath);
    }

    [Fact]
    public void DefaultDataDirectory_IsInLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, LauncherOptions.DefaultDataDirectory);
    }

    [Fact]
    public void GetLauncherVersion_HasNoCommitHash()
    {
        Assert.DoesNotContain("+", LauncherOptions.GetLauncherVersion());
    }
}
