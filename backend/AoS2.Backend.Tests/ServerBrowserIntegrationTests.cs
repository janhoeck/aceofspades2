using System.Diagnostics;
using System.Net.Http.Json;
using AoS2.Contracts;
using AoS2.JoinTickets;
using AoS2.Net;
using AoS2.Server;
using AoS2.Shared.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace AoS2.Backend.Tests;

/// <summary>
/// Ende zu Ende, alles echt bis auf die Datenbank: Ein Spielserver lauscht auf UDP, schickt seinen Heartbeat an das Backend,
/// das Backend prüft ihn per UDP (mit Nachweis), der Server erscheint in der Liste, und der Client misst den Ping direkt.
/// </summary>
[Collection("UDP")]
public sealed class ServerBrowserIntegrationTests : IDisposable
{
    private readonly BackendApp _app = new(new() { ["ServerBrowser:AllowPrivateAddresses"] = "true" }, fakeProbe: false);
    private readonly GameServer _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _loop;

    public ServerBrowserIntegrationTests()
    {
        var options = ServerOptions.Parse(["--dev"]); // Spieldaten und Map aus dem Ausgabeordner, wie neben der exe
        var tickets = new TicketCheck(null, JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, TimeProvider.System), TimeProvider.System);
        _server = new GameServer(GameContent.Load(options), new MatchSettings { PlayersPerTeam = 4 }, tickets, NullLogger.Instance, "Integrationstest");
        _server.Start(0);
        // Hauptschleife des Servers wie im Betrieb; nur dieser Thread fasst den GameServer an.
        _loop = new Thread(() => ServerLoop.Run(_server, _stop.Token)) { IsBackground = true };
        _loop.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loop.Join();
        _server.Stop();
        _server.Dispose();
        _app.Dispose();
    }

    private string Address => $"127.0.0.1:{_server.Port}";

    private Heartbeat CreateHeartbeat(string? address = null) =>
        new(_app.CreateClient(), "http://localhost", _server, address ?? Address, null, NullLogger.Instance);

    [Fact]
    public async Task Server_RegistersByHeartbeat_AppearsInTheList_AndAnswersPing()
    {
        var heartbeat = CreateHeartbeat();

        var next = await heartbeat.SendAsync(CancellationToken.None);

        Assert.True(heartbeat.Listed, heartbeat.Problem);
        Assert.Equal(TimeSpan.FromSeconds(30), next);

        // Liste wie im Spiel laden und anzeigen.
        var list = (await _app.CreateClient().GetFromJsonAsync<ServerListResponse>($"{ApiRoutes.Servers}?protocol={Shared.ProtocolInfo.Version}"))!;
        var entry = Assert.Single(list.Servers);
        Assert.Equal((Address, "Integrationstest", "testmap", 0, 8), (entry.Address, entry.Name, entry.Map, entry.Players, entry.MaxPlayers));

        var browser = new ServerList();
        browser.Set(list.Servers);
        var clock = Stopwatch.StartNew();
        using var pinger = new ServerPinger(() => clock.Elapsed.TotalSeconds);
        pinger.Ping(entry.Address);
        while (pinger.ResultFor(entry.Address)!.State == PingState.Pending && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            pinger.Poll();
            Thread.Sleep(1);
        }

        var ping = pinger.ResultFor(entry.Address)!;
        Assert.Equal(PingState.Answered, ping.State);
        Assert.InRange(ping.RoundTripMs, 0, 500);
        Assert.Equal("Integrationstest", ping.Info.Name);
        var row = Assert.Single(browser.Visible(new ServerFilter(), pinger.ResultFor));
        Assert.Equal(ping.RoundTripMs, row.PingMs);

        // Beim Beenden meldet sich der Server ab.
        await heartbeat.RemoveAsync(CancellationToken.None);
        Assert.Empty((await _app.CreateClient().GetFromJsonAsync<ServerListResponse>(ApiRoutes.Servers))!.Servers);
    }

    [Fact]
    public async Task ServerUnderWrongAddress_IsNotListed_AndLogsWhy()
    {
        // Ein Port, auf dem niemand antwortet: wie ein Server, dessen Port im Router nicht weitergeleitet ist.
        using var unused = new System.Net.Sockets.UdpClient(0);
        var deadPort = ((System.Net.IPEndPoint)unused.Client.LocalEndPoint!).Port;
        var heartbeat = CreateHeartbeat($"127.0.0.1:{deadPort}");

        var next = await heartbeat.SendAsync(CancellationToken.None);

        Assert.False(heartbeat.Listed);
        Assert.Contains("nicht erreichbar", heartbeat.Problem);
        Assert.Contains("422", heartbeat.Problem);
        Assert.Equal(Heartbeat.RetryAfterFailure, next);
        Assert.Empty((await _app.CreateClient().GetFromJsonAsync<ServerListResponse>(ApiRoutes.Servers))!.Servers);
    }

    [Fact]
    public async Task AnotherProcess_CannotListThisServerUnderItsOwnName()
    {
        var heartbeat = CreateHeartbeat();
        await heartbeat.SendAsync(CancellationToken.None);
        Assert.True(heartbeat.Listed, heartbeat.Problem);

        // Ein Fremder kennt die Adresse, aber nicht das Geheimnis des Servers.
        var fake = CreateHeartbeat().CreateRequest();
        fake.Name = "Gefälschter Name";
        fake.Secret = Convert.ToBase64String(new byte[32]);
        using var response = await _app.CreateClient().PostAsJsonAsync(ApiRoutes.ServerHeartbeat, fake);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var entry = Assert.Single((await _app.CreateClient().GetFromJsonAsync<ServerListResponse>(ApiRoutes.Servers))!.Servers);
        Assert.Equal("Integrationstest", entry.Name);
    }
}
