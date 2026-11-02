using System.Net;
using System.Net.Http.Json;
using AoS2.Backend.Auth;
using AoS2.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace AoS2.Backend.Tests;

/// <summary>Der OAuth2-Ablauf aus docs/backend.md, Abschnitt 2, mit nachgebildeter Website und nachgebildetem Discord.</summary>
public sealed class LoginFlowTests : IDisposable
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private readonly BackendApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Start_ReturnsDiscordUrl_WithWebsiteCallback()
    {
        var start = await _app.StartLoginAsync(Challenge);

        Assert.StartsWith(FakeDiscord.AuthorizeUrl, start.AuthorizeUrl);
        var query = QueryHelpers.ParseQuery(new Uri(start.AuthorizeUrl).Query);
        Assert.Equal($"{BackendApp.PublicUrl}/auth/discord/callback", query["redirect_uri"].ToString());
        Assert.Equal(start.AttemptId, query["state"].ToString());
        Assert.NotEqual(BackendApp.LauncherState, start.AttemptId);
        Assert.Equal(600, start.MaxAgeSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("falscher-schluessel")]
    [InlineData("")]
    public async Task InternalApi_NeedsWebsiteKey(string? key)
    {
        var website = _app.CreateWebsite(key);

        using var start = await website.PostAsJsonAsync(LoginEndpoints.StartPath, new LoginStartRequest(BackendApp.LauncherPort, BackendApp.LauncherState, Challenge));
        using var complete = await website.PostAsJsonAsync(LoginEndpoints.CompletePath, new LoginCompleteRequest("x", "y", null));

        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, complete.StatusCode);
        Assert.Empty(_app.WithDb(db => db.LoginAttempts.ToList()));
    }

    [Theory]
    [InlineData(80, "launcher-state-0123456789", Challenge)]
    [InlineData(70000, "launcher-state-0123456789", Challenge)]
    [InlineData(50123, "kurz", Challenge)]
    [InlineData(50123, "launcher state 0123456789", Challenge)]
    [InlineData(50123, "launcher-state-0123456789", "zu-kurz")]
    [InlineData(50123, null, Challenge)]
    public async Task Start_RejectsInvalidParameters(int port, string? state, string challenge)
    {
        using var response = await _app.CreateWebsite().PostAsJsonAsync(LoginEndpoints.StartPath, new LoginStartRequest(port, state!, challenge));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FullFlow_CreatesAccount_AndReturnsSession()
    {
        var session = await _app.SignInAsync();

        Assert.StartsWith("aos2_", session.Token);
        Assert.Equal("Jan", session.Account.DisplayName);
        Assert.Equal("https://cdn.discordapp.com/avatars/123456789012345678/a1b2c3.png?size=128", session.Account.AvatarUrl);
        Assert.Equal(_app.Time.GetUtcNow() + SessionAuthentication.Lifetime, session.ExpiresAt);

        var (code, redirectUri) = Assert.Single(_app.Discord.Exchanges);
        Assert.Equal(FakeDiscord.ValidCode, code);
        Assert.Equal($"{BackendApp.PublicUrl}/auth/discord/callback", redirectUri);

        var me = await _app.CreateClient(session.Token).GetFromJsonAsync<AccountInfo>(ApiRoutes.Me);
        Assert.Equal(session.Account.Id, me!.Id);
        Assert.Equal("Jan", me.DisplayName);
    }

    [Fact]
    public async Task Database_StoresOnlyHashes()
    {
        var session = await _app.SignInAsync();

        var stored = _app.WithDb(db => db.Sessions.Single());
        Assert.NotEqual(session.Token, stored.TokenHash);
        Assert.Equal(SecretTokens.Hash(session.Token), stored.TokenHash);
        Assert.Empty(_app.WithDb(db => db.LoginAttempts.ToList()));
    }

    [Fact]
    public async Task Complete_UnknownAttempt_IsExpired()
    {
        using var response = await _app.CompleteLoginAsync("unbekannt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_app.Discord.Exchanges);
    }

    [Fact]
    public async Task Complete_Twice_IsRejected()
    {
        var start = await _app.StartLoginAsync(Challenge);
        using var first = await _app.CompleteLoginAsync(start.AttemptId);

        using var second = await _app.CompleteLoginAsync(start.AttemptId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Complete_AfterTenMinutes_IsRejected()
    {
        var start = await _app.StartLoginAsync(Challenge);

        _app.Time.Advance(LoginEndpoints.AttemptLifetime + TimeSpan.FromSeconds(1));
        using var response = await _app.CompleteLoginAsync(start.AttemptId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Complete_UserDenied_TellsLauncher()
    {
        var start = await _app.StartLoginAsync(Challenge);

        using var response = await _app.CompleteLoginAsync(start.AttemptId, code: null, error: "access_denied");

        var query = await BackendApp.ParseLoopbackAsync(response);
        Assert.Equal(LoginErrors.AccessDenied, query[LoginParameters.Error]);
        Assert.False(query.ContainsKey(LoginParameters.Code));
        Assert.Empty(_app.WithDb(db => db.LoginAttempts.ToList()));
    }

    [Fact]
    public async Task Complete_DiscordFails_TellsLauncher()
    {
        _app.Discord.Fail = true;
        var start = await _app.StartLoginAsync(Challenge);

        using var response = await _app.CompleteLoginAsync(start.AttemptId);

        Assert.Equal(LoginErrors.DiscordFailed, (await BackendApp.ParseLoopbackAsync(response))[LoginParameters.Error]);
        Assert.Empty(_app.WithDb(db => db.Accounts.ToList()));
    }

    [Fact]
    public async Task Exchange_WrongVerifier_FailsAndUsesUpCode()
    {
        var (code, verifier) = await _app.LoginInBrowserAsync();

        using var wrong = await _app.ExchangeAsync(code, BackendApp.CreatePkce().Verifier);
        using var right = await _app.ExchangeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Empty(_app.WithDb(db => db.Sessions.ToList()));
    }

    [Fact]
    public async Task Exchange_CodeWorksOnlyOnce()
    {
        var (code, verifier) = await _app.LoginInBrowserAsync();

        using var first = await _app.ExchangeAsync(code, verifier);
        using var second = await _app.ExchangeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Exchange_AfterTwoMinutes_Fails()
    {
        var (code, verifier) = await _app.LoginInBrowserAsync();

        _app.Time.Advance(LoginEndpoints.CodeLifetime + TimeSpan.FromSeconds(1));
        using var response = await _app.ExchangeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("unbekannt", "egal")]
    public async Task Exchange_InvalidCode_Fails(string code, string verifier)
    {
        using var response = await _app.ExchangeAsync(code, verifier);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SecondLogin_SameAccount_UpdatesName()
    {
        var first = await _app.SignInAsync();
        _app.Discord.User = _app.Discord.User with { GlobalName = "Jan der Zweite", Avatar = null };

        var second = await _app.SignInAsync();

        Assert.Equal(first.Account.Id, second.Account.Id);
        Assert.Equal("Jan der Zweite", second.Account.DisplayName);
        Assert.Null(second.Account.AvatarUrl);
        Assert.NotEqual(first.Token, second.Token);
        Assert.Single(_app.WithDb(db => db.Accounts.ToList()));
    }

    [Fact]
    public async Task OtherDiscordUser_GetsOwnAccount()
    {
        var first = await _app.SignInAsync();
        _app.Discord.User = new DiscordUser("999", "max", null, null);

        var second = await _app.SignInAsync();

        Assert.NotEqual(first.Account.Id, second.Account.Id);
        Assert.Equal("max", second.Account.DisplayName);
    }
}
