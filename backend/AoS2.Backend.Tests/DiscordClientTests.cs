using System.Net;
using System.Text;
using AoS2.Backend.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Tests;

/// <summary>Der echte Discord-Client gegen eine nachgebildete Discord-API.</summary>
public sealed class DiscordClientTests
{
    private readonly FakeHandler _handler = new();
    private readonly DiscordClient _client;

    public DiscordClientTests()
    {
        var options = Options.Create(new DiscordOptions { ClientId = "1234", ClientSecret = "geheim" });
        _client = new DiscordClient(new HttpClient(_handler), options);
    }

    [Fact]
    public void AuthorizeUrl_AsksOnlyForIdentify()
    {
        var url = new Uri(_client.GetAuthorizeUrl("state-1", "https://api.test/auth/discord/callback"));
        var query = QueryHelpers.ParseQuery(url.Query).ToDictionary(p => p.Key, p => p.Value.ToString());

        Assert.Equal("https://discord.com/oauth2/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("1234", query["client_id"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("https://api.test/auth/discord/callback", query["redirect_uri"]);
    }

    [Fact]
    public async Task ExchangeCode_SendsSecretOnlyInHeader()
    {
        _handler.Respond("""{"access_token":"tok","token_type":"Bearer"}""");

        var token = await _client.ExchangeCodeAsync("code-1", "https://api.test/cb", default);

        Assert.Equal("tok", token);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://discord.com/api/v10/oauth2/token", request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("1234:geheim")), request.Authorization);
        Assert.Equal("grant_type=authorization_code&code=code-1&redirect_uri=https%3A%2F%2Fapi.test%2Fcb", request.Body);
    }

    [Fact]
    public async Task GetUser_ReadsIdNameAndAvatar()
    {
        _handler.Respond("""{"id":"80351110224678912","username":"nelly","global_name":"Nelly","avatar":"8342729096ea3675442027381ff50dfe","discriminator":"0"}""");

        var user = await _client.GetUserAsync("tok", default);

        Assert.Equal(new DiscordUser("80351110224678912", "nelly", "Nelly", "8342729096ea3675442027381ff50dfe"), user);
        Assert.Equal("Nelly", user.DisplayName);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://discord.com/api/v10/users/@me", request.Uri);
        Assert.Equal("Bearer tok", request.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DisplayName_FallsBackToUsername(string? globalName) =>
        Assert.Equal("nelly", new DiscordUser("1", "nelly", globalName, null).DisplayName);

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.OK, "kein json")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task BadAnswer_ThrowsDiscordException(HttpStatusCode status, string body)
    {
        _handler.Respond(body, status);

        await Assert.ThrowsAsync<DiscordException>(() => _client.ExchangeCodeAsync("c", "r", default));
    }

    [Fact]
    public async Task NoConnection_ThrowsDiscordException()
    {
        _handler.Error = new HttpRequestException("offline");

        await Assert.ThrowsAsync<DiscordException>(() => _client.GetUserAsync("tok", default));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public Exception? Error { get; set; }
        public List<(HttpMethod Method, string Uri, string? Authorization, string? Body)> Requests { get; } = [];

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK) => (_body, _status) = (body, status);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Error is not null)
                throw Error;
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
