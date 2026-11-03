using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AoS2.Backend.Auth;
using AoS2.Backend.Data;
using AoS2.Backend.Servers;
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
/// Die Tests übernehmen die Rolle der Website (interne Login-API mit Schlüssel).
/// </summary>
internal sealed class BackendApp : WebApplicationFactory<Program>
{
    public const string PublicUrl = "https://aos2.test";
    public const string InternalKey = "test-schluessel-0123456789abcdef0123456789";
    public const int LauncherPort = 50123;
    public const string LauncherState = "launcher-state-0123456789";

    public static readonly JoinTicketSigningKey SigningKey = JoinTicketSigningKey.Generate();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;
    private readonly bool _fakeDiscord;
    private readonly bool _fakeProbe;

    public BackendApp(Dictionary<string, string?>? settings = null, string environment = "Production", bool fakeDiscord = true, bool fakeProbe = true)
    {
        _environment = environment;
        _fakeDiscord = fakeDiscord;
        _fakeProbe = fakeProbe;
        _settings = new()
        {
            ["Backend:PublicUrl"] = PublicUrl,
            ["Backend:InternalKey"] = InternalKey,
            ["Backend:MigrateOnStartup"] = "false",
            ["Discord:ClientId"] = "client-id",
            ["Discord:ClientSecret"] = "client-secret",
            ["JoinTickets:SigningKey"] = SigningKey.ExportPrivateKey(),
        };
        foreach (var (key, value) in settings ?? [])
            _settings[key] = value;
        _connection.Open();
    }

    public FakeDiscord Discord { get; } = new();

    /// <summary>Statt der UDP-Erreichbarkeitsprüfung (außer mit <c>fakeProbe: false</c>).</summary>
    public FakeServerProbe Probe { get; } = new();
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

            if (_fakeProbe)
            {
                services.RemoveAll<IServerProbe>();
                services.AddSingleton<IServerProbe>(Probe);
            }
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackendDb>().Database.EnsureCreated();
    }

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

    // ---------------------------------------------------------------- Die Website (interne Login-API)

    public HttpClient CreateWebsite(string? key = InternalKey)
    {
        var client = CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add(LoginEndpoints.InternalKeyHeader, key);
        return client;
    }

    /// <summary>PKCE: Der Launcher behält den Verifier und schickt nur die Challenge mit.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>Website ruft <c>/login</c> auf: Anmeldung beginnen.</summary>
    public async Task<LoginStartResponse> StartLoginAsync(string challenge)
    {
        using var response = await CreateWebsite().PostAsJsonAsync(LoginEndpoints.StartPath, new LoginStartRequest(LauncherPort, LauncherState, challenge));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginStartResponse>())!;
    }

    /// <summary>Discord ist zurück bei der Website, die Website meldet das dem Backend.</summary>
    public async Task<HttpResponseMessage> CompleteLoginAsync(string attemptId, string? code = FakeDiscord.ValidCode, string? error = null) =>
        await CreateWebsite().PostAsJsonAsync(LoginEndpoints.CompletePath, new LoginCompleteRequest(attemptId, code, error));

    /// <summary>Kompletter Browser-Teil bis zur Weiterleitung an den Launcher. Gibt den Einmal-Code zurück.</summary>
    public async Task<(string Code, string Verifier)> LoginInBrowserAsync()
    {
        var (verifier, challenge) = CreatePkce();
        var start = await StartLoginAsync(challenge);
        using var complete = await CompleteLoginAsync(start.AttemptId);
        var query = await ParseLoopbackAsync(complete);
        return (query[LoginParameters.Code], verifier);
    }

    /// <summary>Prüft die Weiterleitungsadresse zum Launcher und gibt ihre Query zurück.</summary>
    public static async Task<Dictionary<string, string>> ParseLoopbackAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var location = new Uri((await response.Content.ReadFromJsonAsync<LoginCompleteResponse>())!.RedirectUrl);
        Assert.Equal($"http://127.0.0.1:{LauncherPort}{LoginParameters.LoopbackPath}", location.GetLeftPart(UriPartial.Path));
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
