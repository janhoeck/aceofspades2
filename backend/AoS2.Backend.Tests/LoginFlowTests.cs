using System.Net;
using System.Net.Http.Json;
using AoS2.Backend.Auth;
using AoS2.Contracts;
using Microsoft.AspNetCore.WebUtilities;

namespace AoS2.Backend.Tests;

/// <summary>Der OAuth2-Ablauf aus docs/architecture.md, Abschnitt 6, mit nachgebildetem Discord.</summary>
public sealed class LoginFlowTests : IDisposable
{
    private readonly BackendApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Login_RedirectsToDiscord_AndSetsBrowserCookie()
    {
        var (_, challenge) = BackendApp.CreatePkce();

        using var response = await _app.CreateBrowser().GetAsync(BackendApp.LoginUrl(challenge));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(FakeDiscord.AuthorizeUrl, location.ToString());
        var query = QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        Assert.Equal($"{BackendApp.PublicUrl}/auth/discord/callback", query["redirect_uri"]);
        Assert.NotEqual(BackendApp.LauncherState, query["state"]);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{LoginEndpoints.CookieName}={query["state"]};", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
    }

    [Theory]
    [InlineData("port=80&state=launcher-state-0123456789&code_challenge=" + Challenge)]
    [InlineData("port=70000&state=launcher-state-0123456789&code_challenge=" + Challenge)]
    [InlineData("port=50123&state=kurz&code_challenge=" + Challenge)]
    [InlineData("port=50123&state=launcher%20state%200123456789&code_challenge=" + Challenge)]
    [InlineData("port=50123&state=launcher-state-0123456789&code_challenge=zu-kurz")]
    [InlineData("port=50123&state=launcher-state-0123456789")]
    [InlineData("")]
    public async Task Login_RejectsInvalidParameters(string query)
    {
        using var response = await _app.CreateBrowser().GetAsync($"{ApiRoutes.DiscordLogin}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Ungültige Anmeldung", await response.Content.ReadAsStringAsync());
    }

    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

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
    public async Task Callback_WithoutBrowserCookie_IsRejected()
    {
        var (_, challenge) = BackendApp.CreatePkce();
        var state = await BackendApp.StartLoginAsync(_app.CreateBrowser(), challenge);

        // Ein anderer Browser (z. B. ein untergeschobener Link) hat das Cookie nicht.
        using var response = await BackendApp.DiscordReturnsAsync(_app.CreateBrowser(), state);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Discord.Exchanges);
    }

    [Fact]
    public async Task Callback_UnknownState_IsRejected()
    {
        using var response = await BackendApp.DiscordReturnsAsync(_app.CreateBrowser(), "unbekannt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Anmeldung abgelaufen", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_Twice_IsRejected()
    {
        var browser = _app.CreateBrowser();
        var (_, challenge) = BackendApp.CreatePkce();
        var state = await BackendApp.StartLoginAsync(browser, challenge);
        using var first = await BackendApp.DiscordReturnsAsync(browser, state);

        using var second = await BackendApp.DiscordReturnsAsync(browser, state);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Callback_AfterTenMinutes_IsRejected()
    {
        var browser = _app.CreateBrowser();
        var (_, challenge) = BackendApp.CreatePkce();
        var state = await BackendApp.StartLoginAsync(browser, challenge);

        _app.Time.Advance(LoginEndpoints.AttemptLifetime + TimeSpan.FromSeconds(1));
        using var response = await BackendApp.DiscordReturnsAsync(browser, state);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_UserDenied_TellsLauncher()
    {
        var browser = _app.CreateBrowser();
        var (_, challenge) = BackendApp.CreatePkce();
        var state = await BackendApp.StartLoginAsync(browser, challenge);

        using var response = await browser.GetAsync($"{ApiRoutes.DiscordCallback}?error=access_denied&state={state}");

        var query = BackendApp.ParseLoopback(response);
        Assert.Equal(LoginErrors.AccessDenied, query[LoginParameters.Error]);
        Assert.False(query.ContainsKey(LoginParameters.Code));
        Assert.Empty(_app.WithDb(db => db.LoginAttempts.ToList()));
    }

    [Fact]
    public async Task Callback_DiscordFails_TellsLauncher()
    {
        _app.Discord.Fail = true;
        var browser = _app.CreateBrowser();
        var (_, challenge) = BackendApp.CreatePkce();
        var state = await BackendApp.StartLoginAsync(browser, challenge);

        using var response = await BackendApp.DiscordReturnsAsync(browser, state);

        Assert.Equal(LoginErrors.DiscordFailed, BackendApp.ParseLoopback(response)[LoginParameters.Error]);
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
