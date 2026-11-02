using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AoS2.Backend.Auth;
using AoS2.Backend.Data;
using AoS2.Contracts;
using AoS2.JoinTickets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AoS2.Backend.Tests;

/// <summary>
/// Das Backend im Testserver: SQLite statt PostgreSQL, nachgebildetes Discord, steuerbare Uhr.
/// Umgebung "Production", damit dieselben Prüfungen gelten wie auf dem echten Server.
/// </summary>
internal sealed class BackendApp : WebApplicationFactory<Program>
{
    public const string PublicUrl = "https://api.test";
    public const int LauncherPort = 50123;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;
    private readonly bool _fakeDiscord;

    public BackendApp(Dictionary<string, string?>? settings = null, string environment = "Production", bool fakeDiscord = true)
    {
        _environment = environment;
        _fakeDiscord = fakeDiscord;
        _settings = new()
        {
            ["Backend:PublicUrl"] = PublicUrl,
            ["Backend:MigrateOnStartup"] = "false",
            ["Discord:ClientId"] = "client-id",
            ["Discord:ClientSecret"] = "client-secret",
            ["JoinTickets:SigningKey"] = SigningKey.ExportPrivateKey(),
        };
        foreach (var (key, value) in settings ?? [])
            _settings[key] = value;
        _connection.Open();
    }

    public static readonly JoinTicketSigningKey SigningKey = JoinTicketSigningKey.Generate();

    public FakeDiscord Discord { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<BackendDb>>();
            services.RemoveAll<IDbContextOptionsConfiguration<BackendDb>>();
            services.AddDbContext<BackendDb>(options => options.UseSqlite(_connection).UseSnakeCaseNamingConvention());

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            if (_fakeDiscord)
            {
                services.RemoveAll<IDiscordClient>();
                services.AddSingleton<IDiscordClient>(Discord);
            }
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackendDb>().Database.EnsureCreated();
    }

    /// <summary>Ein Browser: folgt keinen Weiterleitungen (die prüfen die Tests selbst), merkt sich Cookies.</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri(PublicUrl) });

    public T WithDb<T>(Func<BackendDb, T> action)
    {
        using var scope = Services.CreateScope();
        return action(scope.ServiceProvider.GetRequiredService<BackendDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }

    // ---------------------------------------------------------------- Anmeldung, wie der Launcher sie macht

    public const string LauncherState = "launcher-state-0123456789";

    /// <summary>PKCE: Der Launcher behält den Verifier und schickt nur die Challenge mit.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string LoginUrl(string challenge, int port = LauncherPort, string state = LauncherState) =>
        QueryHelpers.AddQueryString(ApiRoutes.DiscordLogin, new Dictionary<string, string?>
        {
            [LoginParameters.Port] = port.ToString(),
            [LoginParameters.State] = state,
            [LoginParameters.CodeChallenge] = challenge,
        });

    /// <summary>Startet die Anmeldung und gibt den <c>state</c> zurück, den das Backend an Discord schickt.</summary>
    public static async Task<string> StartLoginAsync(HttpClient browser, string challenge)
    {
        using var response = await browser.GetAsync(LoginUrl(challenge));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
    }

    public static async Task<HttpResponseMessage> DiscordReturnsAsync(HttpClient browser, string state, string code = FakeDiscord.ValidCode) =>
        await browser.GetAsync($"{ApiRoutes.DiscordCallback}?code={code}&state={state}");

    /// <summary>Kompletter Browser-Teil bis zur Weiterleitung an den Launcher. Gibt den Einmal-Code zurück.</summary>
    public async Task<(string Code, string Verifier)> LoginInBrowserAsync()
    {
        var browser = CreateBrowser();
        var (verifier, challenge) = CreatePkce();
        var state = await StartLoginAsync(browser, challenge);
        using var callback = await DiscordReturnsAsync(browser, state);
        var query = ParseLoopback(callback);
        return (query[LoginParameters.Code], verifier);
    }

    /// <summary>Prüft die Weiterleitung an den Launcher und gibt ihre Query zurück.</summary>
    public static Dictionary<string, string> ParseLoopback(HttpResponseMessage response, int port = LauncherPort)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal($"http://127.0.0.1:{port}{LoginParameters.LoopbackPath}", location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        Assert.Equal(LauncherState, query[LoginParameters.State]);
        return query;
    }

    public async Task<HttpResponseMessage> ExchangeAsync(string code, string verifier) =>
        await CreateClient().PostAsJsonAsync(ApiRoutes.Session, new SessionRequest { Code = code, CodeVerifier = verifier });

    /// <summary>Ganze Anmeldung bis zum Sitzungstoken.</summary>
    public async Task<SessionResponse> SignInAsync()
    {
        var (code, verifier) = await LoginInBrowserAsync();
        using var response = await ExchangeAsync(code, verifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    public HttpClient CreateClient(string sessionToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        return client;
    }
}

/// <summary>Nachgebildetes Discord: kennt genau einen gültigen Code und liefert <see cref="User"/>.</summary>
internal sealed class FakeDiscord : IDiscordClient
{
    public const string ValidCode = "discord-code";
    public const string AuthorizeUrl = "https://discord.test/authorize";

    public DiscordUser User { get; set; } = new("123456789012345678", "jan_h", "Jan", "a1b2c3");
    public bool Fail { get; set; }
    public List<(string Code, string RedirectUri)> Exchanges { get; } = [];

    public string GetAuthorizeUrl(string state, string redirectUri) =>
        QueryHelpers.AddQueryString(AuthorizeUrl, new Dictionary<string, string?> { ["state"] = state, ["redirect_uri"] = redirectUri });

    public Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        Exchanges.Add((code, redirectUri));
        return Fail || code != ValidCode
            ? Task.FromException<string>(new DiscordException("ungültiger Code"))
            : Task.FromResult("discord-access-token");
    }

    public Task<DiscordUser> GetUserAsync(string accessToken, CancellationToken cancellationToken) =>
        accessToken == "discord-access-token" ? Task.FromResult(User) : Task.FromException<DiscordUser>(new DiscordException("ungültiges Token"));
}
