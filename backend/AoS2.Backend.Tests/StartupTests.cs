using System.Net;
using System.Net.Http.Json;
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
    [InlineData("Backend:PublicUrl", "aos2.example.org")]
    [InlineData("Backend:InternalKey", "")]
    [InlineData("Backend:InternalKey", "zu-kurz")]
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
    public async Task Development_AllowsShortKey_AndRandomSigningKey()
    {
        using var app = new BackendApp(new() { ["JoinTickets:SigningKey"] = "", ["Backend:InternalKey"] = "kurz" }, environment: "Development");

        using var response = await app.CreateClient().GetAsync(ApiRoutes.JoinTicketKeys);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DevLogin_WorksWithoutDiscord()
    {
        using var app = new BackendApp(
            new() { ["Discord:UseDevLogin"] = "true", ["Discord:ClientId"] = "", ["Discord:ClientSecret"] = "" },
            environment: "Development", fakeDiscord: false);
        var (verifier, challenge) = BackendApp.CreatePkce();

        // Statt zu Discord geht es zur Entwicklungsseite der Website.
        var start = await app.StartLoginAsync(challenge);
        var devPage = new Uri(start.AuthorizeUrl);
        Assert.Equal($"{BackendApp.PublicUrl}{DevDiscordClient.AuthorizePath}", devPage.GetLeftPart(UriPartial.Path));
        Assert.Equal(start.AttemptId, QueryHelpers.ParseQuery(devPage.Query)["state"].ToString());

        // Die Website schickt den eingegebenen Namen als Code zurück.
        using var complete = await app.CompleteLoginAsync(start.AttemptId, DevDiscordClient.CreateCode("Testspieler"));
        var code = (await BackendApp.ParseLoopbackAsync(complete))[LoginParameters.Code];

        using var exchange = await app.ExchangeAsync(code, verifier);
        var session = await exchange.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.Equal("Testspieler", session!.Account.DisplayName);
    }
}
