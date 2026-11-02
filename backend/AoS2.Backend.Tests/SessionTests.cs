using System.Net;
using System.Net.Http.Json;
using AoS2.Backend.Auth;
using AoS2.Contracts;
using AoS2.JoinTickets;

namespace AoS2.Backend.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly BackendApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer aos2_gibtesnicht")]
    [InlineData("Basic Zm9vOmJhcg==")]
    public async Task Me_WithoutValidSession_Is401(string? authorization)
    {
        var client = _app.CreateClient();
        if (authorization is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.GetAsync(ApiRoutes.Me);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var session = await _app.SignInAsync();
        var client = _app.CreateClient(session.Token);

        using var logout = await client.PostAsync(ApiRoutes.Logout, null);
        using var me = await client.GetAsync(ApiRoutes.Me);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Logout_KeepsOtherSessions()
    {
        var first = await _app.SignInAsync();
        var second = await _app.SignInAsync();

        using var logout = await _app.CreateClient(first.Token).PostAsync(ApiRoutes.Logout, null);
        using var me = await _app.CreateClient(second.Token).GetAsync(ApiRoutes.Me);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Session_ExpiresWhenUnused()
    {
        var session = await _app.SignInAsync();

        _app.Time.Advance(SessionAuthentication.Lifetime + TimeSpan.FromSeconds(1));
        using var me = await _app.CreateClient(session.Token).GetAsync(ApiRoutes.Me);

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Session_StaysValidWhenUsed()
    {
        var session = await _app.SignInAsync();
        var client = _app.CreateClient(session.Token);

        for (var i = 0; i < 3; i++)
        {
            _app.Time.Advance(TimeSpan.FromDays(20));
            using var me = await client.GetAsync(ApiRoutes.Me);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
    }

    [Fact]
    public async Task ExpiredSessions_AreDeletedOnNextLogin()
    {
        await _app.SignInAsync();
        _app.Time.Advance(SessionAuthentication.Lifetime + TimeSpan.FromSeconds(1));

        await _app.SignInAsync();

        Assert.Single(_app.WithDb(db => db.Sessions.ToList()));
    }

    // ---------------------------------------------------------------- Join-Tickets

    [Fact]
    public async Task JoinTicket_CanBeCheckedWithPublishedKey()
    {
        var session = await _app.SignInAsync();
        var client = _app.CreateClient(session.Token);

        using var response = await client.PostAsJsonAsync(ApiRoutes.JoinTickets, new JoinTicketRequest { Server = "play.example.org:32887" });
        var ticket = (await response.Content.ReadFromJsonAsync<JoinTicketResponse>())!;
        var keys = await _app.CreateClient().GetStringAsync(ApiRoutes.JoinTicketKeys);

        var validator = new JoinTicketValidator(keys, BackendApp.PublicUrl, "play.example.org:32887", _app.Time);
        var checkedTicket = await validator.ValidateAsync(ticket.Ticket);
        Assert.NotNull(checkedTicket);
        Assert.Equal(session.Account.Id, checkedTicket.AccountId);
        Assert.Equal("Jan", checkedTicket.DisplayName);
        Assert.Equal(_app.Time.GetUtcNow() + JoinTicketIssuer.Lifetime, ticket.ExpiresAt);

        var otherServer = new JoinTicketValidator(keys, BackendApp.PublicUrl, "other.example.org:32887", _app.Time);
        Assert.Null(await otherServer.ValidateAsync(ticket.Ticket));
    }

    [Fact]
    public async Task JoinTicket_NeedsSession()
    {
        using var response = await _app.CreateClient().PostAsJsonAsync(ApiRoutes.JoinTickets, new JoinTicketRequest { Server = "a:1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task JoinTicket_NeedsServer(string? server)
    {
        var session = await _app.SignInAsync();

        using var response = await _app.CreateClient(session.Token).PostAsJsonAsync(ApiRoutes.JoinTickets, new JoinTicketRequest { Server = server! });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PublicKeySet_ContainsConfiguredKey()
    {
        var json = await _app.CreateClient().GetStringAsync(ApiRoutes.JoinTicketKeys);

        Assert.Equal(BackendApp.SigningKey.ExportPublicKeySet(), json);
    }

    [Fact]
    public async Task Health_ChecksDatabase()
    {
        Assert.Equal("ok", await _app.CreateClient().GetStringAsync(ApiRoutes.Health));
    }
}
