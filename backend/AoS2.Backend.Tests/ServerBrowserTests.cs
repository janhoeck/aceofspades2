using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AoS2.Backend.Servers;
using AoS2.Contracts;
using AoS2.Shared;
using AoS2.Shared.Net;
using Microsoft.AspNetCore.Mvc;

namespace AoS2.Backend.Tests;

/// <summary>
/// Server-Browser im Backend: Heartbeat, Erreichbarkeitsprüfung (nachgebildet), Ablauf, Grenzen pro IP, Liste.
/// Die echte UDP-Prüfung mit einem echten Spielserver: <see cref="ServerBrowserIntegrationTests"/>.
/// </summary>
public sealed class ServerBrowserTests : IDisposable
{
    private const string Address = "203.0.113.5:32887"; // Dokumentations-Netz, gilt als öffentliche Adresse
    private const string OfficialKey = "offizieller-schluessel-0123456789abcdef";

    private readonly BackendApp _app = new(new()
    {
        ["ServerBrowser:MaxServersPerIp"] = "2",
        ["ServerBrowser:MaxProbesPerIp"] = "5",
        ["ServerBrowser:OfficialKey"] = OfficialKey,
    });

    public void Dispose() => _app.Dispose();

    private static ServerHeartbeatRequest Heartbeat(string address = Address, byte[]? secret = null, string name = "Jans Server", int players = 3) => new()
    {
        Address = address,
        Name = name,
        Map = "testmap",
        Players = players,
        MaxPlayers = 32,
        ProtocolVersion = ProtocolInfo.Version,
        GameVersion = "0.7.0",
        Secret = Convert.ToBase64String(secret ?? Secret),
    };

    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private HttpClient From(string ip)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }

    private async Task<HttpResponseMessage> SendAsync(ServerHeartbeatRequest request, string ip = "198.51.100.1") =>
        await From(ip).PostAsJsonAsync(ApiRoutes.ServerHeartbeat, request);

    private async Task<List<ServerListEntry>> ListAsync(string query = "")
    {
        var response = await _app.CreateClient().GetFromJsonAsync<ServerListResponse>(ApiRoutes.Servers + query);
        return response!.Servers;
    }

    private static async Task<string> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title!;

    [Fact]
    public async Task FirstHeartbeat_ProbesServer_ThenItIsListed()
    {
        using var response = await SendAsync(Heartbeat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ServerHeartbeatResponse>())!;
        Assert.Equal(30, body.HeartbeatSeconds);
        Assert.Equal(90, body.ExpirySeconds);
        Assert.False(body.Official);

        var probe = Assert.Single(_app.Probe.Calls);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 32887), probe.EndPoint);
        Assert.Equal(Secret, probe.Secret);

        var server = Assert.Single(await ListAsync());
        Assert.Equal((Address, "Jans Server", "testmap", 3, 32, ProtocolInfo.Version, "0.7.0", false),
            (server.Address, server.Name, server.Map, server.Players, server.MaxPlayers, server.ProtocolVersion, server.GameVersion, server.Official));
    }

    [Fact]
    public async Task NextHeartbeats_UpdateTheEntry_WithoutProbingAgain()
    {
        (await SendAsync(Heartbeat(players: 3))).EnsureSuccessStatusCode();
        _app.Time.Advance(TimeSpan.FromSeconds(30));
        (await SendAsync(Heartbeat(players: 7, name: "Jans Server (voll)"))).EnsureSuccessStatusCode();

        Assert.Single(_app.Probe.Calls);
        var server = Assert.Single(await ListAsync());
        Assert.Equal((7, "Jans Server (voll)"), (server.Players, server.Name));
    }

    [Fact]
    public async Task UnreachableServer_IsRejected_AndNotListed()
    {
        _app.Probe.Outcome = ProbeOutcome.NoAnswer;

        using var response = await SendAsync(Heartbeat());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("nicht erreichbar", await TitleAsync(response));
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task OtherServerAnswering_IsRejected()
    {
        _app.Probe.Outcome = ProbeOutcome.WrongServer;

        using var response = await SendAsync(Heartbeat());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("anderer Spielserver", await TitleAsync(response));
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task ServerWithoutHeartbeat_DropsOutOfTheList_AfterExpiry()
    {
        (await SendAsync(Heartbeat())).EnsureSuccessStatusCode();

        _app.Time.Advance(TimeSpan.FromSeconds(89));
        Assert.Single(await ListAsync());

        _app.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task HeartbeatAfterExpiry_ProbesAgain()
    {
        (await SendAsync(Heartbeat())).EnsureSuccessStatusCode();
        _app.Time.Advance(TimeSpan.FromMinutes(5));

        (await SendAsync(Heartbeat())).EnsureSuccessStatusCode();

        Assert.Equal(2, _app.Probe.Calls.Count);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task SomeoneElse_CannotTakeOverAListedAddress()
    {
        (await SendAsync(Heartbeat(name: "Echter Server"))).EnsureSuccessStatusCode();

        // Anderes Geheimnis für dieselbe Adresse: neue Prüfung. Der echte Server kennt dieses Geheimnis nicht.
        _app.Probe.Outcome = ProbeOutcome.WrongServer;
        using var takeover = await SendAsync(Heartbeat(secret: RandomNumberGenerator.GetBytes(32), name: "Fälschung", players: 0), "192.0.2.99");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, takeover.StatusCode);
        Assert.Equal("Echter Server", Assert.Single(await ListAsync()).Name);
    }

    [Fact]
    public async Task RestartedServer_WithNewSecret_IsProbedAndKeepsItsPlace()
    {
        (await SendAsync(Heartbeat(name: "Vor dem Neustart"))).EnsureSuccessStatusCode();

        (await SendAsync(Heartbeat(secret: RandomNumberGenerator.GetBytes(32), name: "Nach dem Neustart"))).EnsureSuccessStatusCode();

        Assert.Equal(2, _app.Probe.Calls.Count);
        Assert.Equal("Nach dem Neustart", Assert.Single(await ListAsync()).Name);
    }

    [Fact]
    public async Task ServersPerIp_AreLimited()
    {
        (await SendAsync(Heartbeat("203.0.113.5:1001"), "198.51.100.7")).EnsureSuccessStatusCode();
        (await SendAsync(Heartbeat("203.0.113.5:1002"), "198.51.100.7")).EnsureSuccessStatusCode();

        using var third = await SendAsync(Heartbeat("203.0.113.5:1003"), "198.51.100.7");
        using var otherIp = await SendAsync(Heartbeat("203.0.113.6:1003"), "198.51.100.8");
        // Bereits eingetragene Server derselben IP schicken weiter Heartbeats.
        using var again = await SendAsync(Heartbeat("203.0.113.5:1001"), "198.51.100.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("höchstens 2", await TitleAsync(third));
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(3, (await ListAsync()).Count);
    }

    [Fact]
    public async Task ServersPerIp_FreeUpWhenServersExpire()
    {
        (await SendAsync(Heartbeat("203.0.113.5:1001"), "198.51.100.7")).EnsureSuccessStatusCode();
        (await SendAsync(Heartbeat("203.0.113.5:1002"), "198.51.100.7")).EnsureSuccessStatusCode();
        _app.Time.Advance(TimeSpan.FromSeconds(91));

        using var response = await SendAsync(Heartbeat("203.0.113.5:1003"), "198.51.100.7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProbesPerIp_AreLimited()
    {
        _app.Probe.Outcome = ProbeOutcome.NoAnswer;
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(Heartbeat(), "198.51.100.9")).StatusCode);

        using var limited = await SendAsync(Heartbeat(), "198.51.100.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(5, _app.Probe.Calls.Count);
    }

    [Theory]
    [InlineData("127.0.0.1:32887")]
    [InlineData("192.168.1.20:32887")]
    [InlineData("10.0.0.5:32887")]
    [InlineData("100.64.1.1:32887")]
    [InlineData("[::1]:32887")]
    [InlineData("[fd00::1]:32887")]
    [InlineData("localhost:32887")]
    public async Task PrivateAddresses_AreRejected_WithoutProbe(string address)
    {
        using var response = await SendAsync(Heartbeat(address));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("keine öffentliche Adresse", await TitleAsync(response));
        Assert.Empty(_app.Probe.Calls);
    }

    [Fact]
    public async Task PrivateAddresses_CanBeAllowedForTesting()
    {
        using var app = new BackendApp(new() { ["ServerBrowser:AllowPrivateAddresses"] = "true" });

        using var response = await app.CreateClient().PostAsJsonAsync(ApiRoutes.ServerHeartbeat, Heartbeat("127.0.0.1:40000"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 40000), Assert.Single(app.Probe.Calls).EndPoint);
    }

    [Fact]
    public async Task UnresolvableName_IsRejected()
    {
        using var response = await SendAsync(Heartbeat("gibt-es-nicht.invalid:32887"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("DNS", await TitleAsync(response));
    }

    public static TheoryData<string, Action<ServerHeartbeatRequest>> InvalidHeartbeats => new()
    {
        { "ohne Port", r => r.Address = "203.0.113.5" },
        { "Adresse kaputt", r => r.Address = "a b:1" },
        { "Name leer", r => r.Name = "" },
        { "Name zu lang", r => r.Name = new string('x', 41) },
        { "Name mit Umbruch", r => r.Name = "a\nb" },
        { "Map mit Pfad", r => r.Map = "../x" },
        { "mehr Spieler als Plätze", r => r.Players = 33 },
        { "keine Plätze", r => r.MaxPlayers = 0 },
        { "Geheimnis zu kurz", r => r.Secret = Convert.ToBase64String(new byte[16]) },
        { "Geheimnis kein Base64", r => r.Secret = "%%%" },
        { "Geheimnis fehlt", r => r.Secret = null },
    };

    [Theory]
    [MemberData(nameof(InvalidHeartbeats))]
    public async Task InvalidHeartbeat_Is400(string _, Action<ServerHeartbeatRequest> change)
    {
        var request = Heartbeat();
        change(request);

        using var response = await SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Probe.Calls);
    }

    [Fact]
    public async Task List_CanFilterByProtocolVersion()
    {
        (await SendAsync(Heartbeat("203.0.113.5:1001"))).EnsureSuccessStatusCode();
        var old = Heartbeat("203.0.113.6:1001", RandomNumberGenerator.GetBytes(32));
        old.ProtocolVersion = ProtocolInfo.Version - 1;
        _app.Probe.ProtocolVersion = old.ProtocolVersion;
        (await SendAsync(old, "198.51.100.2")).EnsureSuccessStatusCode();

        Assert.Equal(2, (await ListAsync()).Count);
        Assert.Equal("203.0.113.5:1001", Assert.Single(await ListAsync($"?protocol={ProtocolInfo.Version}")).Address);
    }

    [Fact]
    public async Task ProtocolInHeartbeat_MustMatchTheServersAnswer()
    {
        _app.Probe.ProtocolVersion = ProtocolInfo.Version + 1;

        using var response = await SendAsync(Heartbeat());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task OfficialServers_AreMarked_ComeFirst_AndIgnoreTheIpLimit()
    {
        (await SendAsync(Heartbeat("203.0.113.5:1001", players: 20))).EnsureSuccessStatusCode();
        for (var port = 2001; port <= 2003; port++)
        {
            var official = Heartbeat($"203.0.113.10:{port}", RandomNumberGenerator.GetBytes(32), players: 0);
            official.OfficialKey = OfficialKey;
            using var response = await SendAsync(official, "198.51.100.50");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<ServerHeartbeatResponse>())!.Official);
        }

        var list = await ListAsync();

        Assert.Equal(4, list.Count);
        Assert.All(list.Take(3), s => Assert.True(s.Official));
        Assert.Equal("203.0.113.5:1001", list[3].Address);
    }

    [Fact]
    public async Task WrongOfficialKey_Is403()
    {
        var request = Heartbeat();
        request.OfficialKey = "falsch-falsch-falsch-falsch-falsch-falsch";

        using var response = await SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task Remove_WithTheSecret_TakesTheServerOut()
    {
        (await SendAsync(Heartbeat())).EnsureSuccessStatusCode();

        using var wrong = await From("192.0.2.1").PostAsJsonAsync(ApiRoutes.ServerRemove,
            new ServerRemoveRequest { Address = Address, Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        Assert.Single(await ListAsync());

        using var right = await From("198.51.100.1").PostAsJsonAsync(ApiRoutes.ServerRemove,
            new ServerRemoveRequest { Address = Address, Secret = Convert.ToBase64String(Secret) });

        Assert.Equal(HttpStatusCode.NoContent, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task OldEntries_AreDeletedFromTheDatabase()
    {
        (await SendAsync(Heartbeat("203.0.113.5:1001"))).EnsureSuccessStatusCode();
        _app.Time.Advance(TimeSpan.FromMinutes(11));

        (await SendAsync(Heartbeat("203.0.113.5:1002", RandomNumberGenerator.GetBytes(32)))).EnsureSuccessStatusCode();

        Assert.Equal(new[] { "203.0.113.5:1002" }, _app.WithDb(db => db.ListedServers.Select(s => s.Address).ToList()));
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("203.0.113.5", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    public void PublicAddressCheck(string ip, bool isPublic) => Assert.Equal(isPublic, ServerEndpoints.IsPublic(IPAddress.Parse(ip)));
}

/// <summary>Nachgebildete Erreichbarkeitsprüfung: merkt sich die Aufrufe und antwortet wie eingestellt.</summary>
internal sealed class FakeServerProbe : IServerProbe
{
    public ProbeOutcome Outcome { get; set; } = ProbeOutcome.Reachable;
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    public List<(IPEndPoint EndPoint, byte[] Secret)> Calls { get; } = [];

    public Task<(ProbeOutcome Outcome, ServerInfo Info)> ProbeAsync(IPEndPoint endPoint, byte[] secret, CancellationToken cancellationToken)
    {
        lock (Calls) Calls.Add((endPoint, secret));
        return Task.FromResult((Outcome, Outcome == ProbeOutcome.NoAnswer ? default : new ServerInfo { ProtocolVersion = ProtocolVersion }));
    }
}
