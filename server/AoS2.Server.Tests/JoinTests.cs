using AoS2.JoinTickets;
using AoS2.Shared;
using AoS2.Shared.Net;
using Microsoft.Extensions.Time.Testing;

namespace AoS2.Server.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void DevMode_IsOffUnlessRequested()
    {
        var options = ServerOptions.Parse(["--site", "https://aos2.example.org/", "--address", "play.example.org:32887"]);
        Assert.False(options.DevMode);
        Assert.Equal("https://aos2.example.org", options.SiteUrl);
        Assert.Equal(ProtocolInfo.DefaultPort, options.Port);
        Assert.False(new ServerOptions().DevMode);
    }

    [Fact]
    public void WithoutSiteOrDev_RefusesToStart()
    {
        var e = Assert.Throws<ArgumentException>(() => ServerOptions.Parse([]));
        Assert.Contains("--dev", e.Message);
    }

    [Fact]
    public void Dev_AllowsStartWithoutBackend()
    {
        var options = ServerOptions.Parse(["--dev", "--port", "4000", "--map", "arena", "--players-per-team", "2"]);
        Assert.True(options.DevMode);
        Assert.Null(options.SiteUrl);
        Assert.Equal((4000, "arena", 4), (options.Port, options.Map, options.MaxPlayers));
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "x")]
    [InlineData("--players-per-team", "25")]
    [InlineData("--score-limit", "0")]
    [InlineData("--map", "../geheim")]
    [InlineData("--site", "ftp://example.org")]
    [InlineData("--unbekannt", "1")]
    public void InvalidValues_Throw(string name, string value) =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--dev", name, value]));

    [Fact]
    public void Site_RequiresAddress() =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--site", "https://aos2.example.org"]));

    [Fact]
    public void MissingValue_Throws() => Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--dev", "--port"]));

    [Fact]
    public void Defaults_TeamDeathmatch16v16To50()
    {
        var options = ServerOptions.Parse(["--dev"]);
        Assert.Equal((16, 50, 0, 15, false), (options.Match.PlayersPerTeam, options.Match.ScoreLimit, options.Match.TimeLimitMinutes,
            options.Match.MatchEndSeconds, options.Match.FriendlyFire));
        Assert.Equal(32, options.MaxPlayers);
    }

    // ---- Config-Datei ----

    private static string WriteConfig(string json)
    {
        var dir = Directory.CreateTempSubdirectory("aos2-config-");
        var path = Path.Combine(dir.FullName, "server.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ConfigFile_SetsEverything_CommandLineWins()
    {
        var path = WriteConfig("""
            // Kommentare sind erlaubt
            {
              "name": "Freitagsrunde",
              "port": 4000,
              "map": "arena",
              "playersPerTeam": 8,
              "scoreLimit": 30,
              "timeLimitMinutes": 20,
              "matchEndSeconds": 10,
              "friendlyFire": true,
              "data": "spieldaten",
              "maps": "karten",
              "site": "https://aos2.example.org/",
              "address": "play.example.org:4000",
              "dev": false,
            }
            """);
        var options = ServerOptions.Parse(["--config", path, "--score-limit", "5"]);
        Assert.Equal("Freitagsrunde", options.Name);
        Assert.Equal((4000, "arena"), (options.Port, options.Map));
        Assert.Equal(new MatchSettings { PlayersPerTeam = 8, ScoreLimit = 5, TimeLimitMinutes = 20, MatchEndSeconds = 10, FriendlyFire = true }, options.Match);
        Assert.Equal(16, options.MaxPlayers);
        var folder = Path.GetDirectoryName(path)!;
        Assert.Equal(Path.Combine(folder, "spieldaten"), options.DataFolder);
        Assert.Equal(Path.Combine(folder, "karten"), options.MapsFolder);
        Assert.Equal(("https://aos2.example.org", "play.example.org:4000"), (options.SiteUrl, options.Address));
        Assert.False(options.DevMode);
        Assert.Equal(path, options.ConfigFile);
    }

    [Theory]
    [InlineData("""{ "scoreLimt": 10, "dev": true }""", "scoreLimt")]
    [InlineData("""{ "playersPerTeam": 25, "dev": true }""", "playersPerTeam")]
    [InlineData("""{ "friendlyFire": "ja", "dev": true }""", "friendlyFire")]
    [InlineData("""{ "port": 1.5, "dev": true }""", "port")]
    [InlineData("""[ 1, 2 ]""", "JSON-Objekt")]
    [InlineData("""{ "dev": true""", "JSON")]
    [InlineData("""{ "map": "../geheim", "dev": true }""", "map")]
    [InlineData("""{ }""", "--dev")]
    public void ConfigFile_Invalid_ThrowsWithReason(string json, string mentioned)
    {
        var e = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", WriteConfig(json)]));
        Assert.Contains(mentioned, e.Message);
    }

    [Fact]
    public void ConfigFile_Missing_Throws() =>
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(["--config", Path.Combine(Path.GetTempPath(), "gibt-es-nicht.json")]));

    [Fact]
    public void ExampleConfig_IsValid_AndMatchesDefaults()
    {
        var options = ServerOptions.Parse(["--config", TestArena.RepoFile("server/AoS2.Server/server.example.json")]);
        Assert.Equal(new MatchSettings(), options.Match);
        Assert.Equal(new ServerOptions().Port, options.Port);
        Assert.Equal("testmap", options.Map);
        Assert.False(options.DevMode);
        Assert.NotNull(options.SiteUrl);

        // Neben der exe liegt eine Kopie (zum Kopieren und Anpassen).
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "server.example.json")));
    }
}

public sealed class TicketCheckTests : IDisposable
{
    private const string Site = "https://aos2.example.org";
    private const string Address = "play.example.org:32887";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly JoinTicketSigningKey _key = JoinTicketSigningKey.Generate();

    public void Dispose() => _key.Dispose();

    private JoinTicketValidator Backend() => new(_key.ExportPublicKeySet(), Site, Address, _time);
    private JoinTicketValidator Development() => JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, _time);

    [Fact]
    public async Task DevTicket_AcceptedInDevMode()
    {
        var check = new TicketCheck(null, Development(), _time);
        var account = Guid.NewGuid();
        var ticket = await check.ValidateAsync(DevJoinTicket.Create(account, "Jan", _time.GetUtcNow()));

        Assert.NotNull(ticket);
        Assert.Equal(account, ticket.AccountId);
        Assert.Equal("Jan", ticket.DisplayName);
    }

    [Fact]
    public async Task DevTicket_RejectedByNormalServer()
    {
        var check = new TicketCheck(Backend(), null, _time);
        Assert.False(check.AcceptsDevelopmentTickets);
        Assert.Null(await check.ValidateAsync(DevJoinTicket.Create(Guid.NewGuid(), "Jan", _time.GetUtcNow())));
        Assert.Contains("--dev", check.InvalidTicketMessage);
    }

    [Fact]
    public async Task BackendTicket_Accepted_AlsoInDevMode()
    {
        var issued = new JoinTicketIssuer(_key, Site, _time).Issue(Guid.NewGuid(), "Jan", Address).Ticket;
        Assert.NotNull(await new TicketCheck(Backend(), null, _time).ValidateAsync(issued));
        Assert.NotNull(await new TicketCheck(Backend(), Development(), _time).ValidateAsync(issued));
    }

    [Fact]
    public async Task ForgedDevTicket_Rejected()
    {
        // Gleiches Format, anderer Schlüssel: Auch im Entwicklungsmodus muss die Signatur stimmen.
        var ticket = DevJoinTicket.Create(Guid.NewGuid(), "Jan", _time.GetUtcNow());
        var forged = ticket[..^4] + (ticket[^4..] == "AAAA" ? "BBBB" : "AAAA");
        Assert.Null(await new TicketCheck(null, Development(), _time).ValidateAsync(forged));
    }

    [Fact]
    public async Task ExpiredDevTicket_Rejected()
    {
        var ticket = DevJoinTicket.Create(Guid.NewGuid(), "Jan", _time.GetUtcNow());
        _time.Advance(DevJoinTicket.Lifetime + JoinTicketValidator.ClockSkew + TimeSpan.FromSeconds(1));
        Assert.Null(await new TicketCheck(null, Development(), _time).ValidateAsync(ticket));
    }

    [Fact]
    public async Task Ticket_CanOnlyBeUsedOnce()
    {
        var check = new TicketCheck(null, Development(), _time);
        var ticket = (await check.ValidateAsync(DevJoinTicket.Create(Guid.NewGuid(), "Jan", _time.GetUtcNow())))!;
        Assert.True(check.TryUse(ticket));
        Assert.False(check.TryUse(ticket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein.jwt")]
    [InlineData("a.b.c")]
    public async Task Garbage_Rejected(string ticket) =>
        Assert.Null(await new TicketCheck(Backend(), Development(), _time).ValidateAsync(ticket));
}
