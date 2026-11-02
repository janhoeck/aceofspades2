using System.Net;
using AoS2.Backend.Auth;
using AoS2.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Tests;

/// <summary>Fehlende oder gefährliche Einstellungen sollen den Start verhindern, nicht erst den ersten Login.</summary>
public sealed class StartupTests
{
    [Theory]
    [InlineData("Backend:PublicUrl", "")]
    [InlineData("Backend:PublicUrl", "api.example.org")]
    [InlineData("Discord:ClientSecret", "")]
    [InlineData("JoinTickets:SigningKey", "")]
    [InlineData("JoinTickets:SigningKey", "kaputt")]
    public void MissingSetting_PreventsStart(string key, string value)
    {
        using var app = new BackendApp(new() { [key] = value });

        Assert.ThrowsAny<Exception>(() => app.CreateClient());
    }

    [Fact]
    public void DevLogin_OutsideDevelopment_PreventsStart()
    {
        using var app = new BackendApp(new() { ["Discord:UseDevLogin"] = "true" });

        var error = Assert.ThrowsAny<OptionsValidationException>(() => app.CreateClient());
        Assert.Contains("UseDevLogin", error.Message);
    }

    [Fact]
    public async Task DevelopmentWithoutSigningKey_UsesRandomKey()
    {
        using var app = new BackendApp(new() { ["JoinTickets:SigningKey"] = "" }, environment: "Development");

        using var response = await app.CreateClient().GetAsync(ApiRoutes.JoinTicketKeys);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DevLogin_WorksWithoutDiscord()
    {
        using var app = new BackendApp(
            new() { ["Discord:UseDevLogin"] = "true", ["Discord:ClientId"] = "", ["Discord:ClientSecret"] = "" },
            environment: "Development", fakeDiscord: false);
        var browser = app.CreateBrowser();
        var (verifier, challenge) = BackendApp.CreatePkce();

        // Login → Entwicklungsseite statt Discord
        using var login = await browser.GetAsync(BackendApp.LoginUrl(challenge));
        var devPage = login.Headers.Location!;
        Assert.Equal(DevDiscordClient.AuthorizePath, devPage.AbsolutePath);
        using var form = await browser.GetAsync(devPage.PathAndQuery);
        Assert.Contains("Entwicklungs-Login", await form.Content.ReadAsStringAsync());

        // Name eingeben → zurück zum Backend → weiter zum Launcher
        var state = QueryHelpers.ParseQuery(devPage.Query)["state"];
        using var approve = await browser.GetAsync($"{LoginEndpoints.DevApprovePath}?state={state}&name=Testspieler");
        using var callback = await browser.GetAsync(approve.Headers.Location!.PathAndQuery);
        var code = BackendApp.ParseLoopback(callback)[LoginParameters.Code];

        using var exchange = await app.ExchangeAsync(code, verifier);
        var session = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<SessionResponse>(exchange.Content);
        Assert.Equal("Testspieler", session!.Account.DisplayName);
    }
}
