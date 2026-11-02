using System.Threading.RateLimiting;
using AoS2.Backend.Auth;
using AoS2.Backend.Data;
using AoS2.Contracts;
using AoS2.JoinTickets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AoS2.Backend;

public sealed class BackendOptions
{
    public const string Section = "Backend";

    /// <summary>Kürzeste Länge für <see cref="InternalKey"/> außerhalb der Entwicklung.</summary>
    public const int MinInternalKeyLength = 32;

    /// <summary>
    /// Öffentliche Adresse der Website ohne „/“ am Ende, z. B. <c>https://aos2.example.org</c>.
    /// Das Backend selbst ist darunter als <c>/api</c> erreichbar (die Website reicht es durch).
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// Gemeinsamer Schlüssel von Website und Backend für <c>/api/internal/…</c>. Die Website schickt ihn
    /// im Header <c>X-Internal-Key</c>; niemand sonst kennt ihn.
    /// </summary>
    public string InternalKey { get; set; } = "";

    /// <summary>Spielt beim Start fehlende Migrationen in die Datenbank ein.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Seite der Website, zu der Discord zurückleitet. Muss genau so als Redirect in der Discord-App stehen.</summary>
    public string CallbackUrl => PublicUrl.TrimEnd('/') + SiteRoutes.DiscordCallback;
}

internal static class RateLimits
{
    public const string Login = "login";
}

/// <summary>Setzt das Backend zusammen. Konfiguration: appsettings.json, Umgebungsvariablen, user-secrets (siehe docs/backend.md).</summary>
public static class BackendSetup
{
    public static void AddBackend(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        var isDevelopment = builder.Environment.IsDevelopment();

        services.AddOptions<BackendOptions>().Bind(config.GetSection(BackendOptions.Section))
            .Validate(o => Uri.TryCreate(o.PublicUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                "Backend:PublicUrl fehlt oder ist keine http(s)-Adresse.")
            .Validate(o => isDevelopment ? o.InternalKey.Length > 0 : o.InternalKey.Length >= BackendOptions.MinInternalKeyLength,
                $"Backend:InternalKey fehlt oder ist kürzer als {BackendOptions.MinInternalKeyLength} Zeichen.")
            .ValidateOnStart();
        services.AddOptions<DiscordOptions>().Bind(config.GetSection(DiscordOptions.Section))
            .Validate(o => o.UseDevLogin || (!string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret)),
                "Discord:ClientId und Discord:ClientSecret fehlen (oder Discord:UseDevLogin zum Entwickeln einschalten).")
            .Validate(o => !o.UseDevLogin || isDevelopment, "Discord:UseDevLogin ist nur in der Development-Umgebung erlaubt.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<BackendDb>((provider, options) => options.UseBackendDatabase(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres fehlt.")));

        services.AddHttpClient(nameof(DiscordClient), http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddTransient<IDiscordClient>(provider =>
        {
            var discord = provider.GetRequiredService<IOptions<DiscordOptions>>();
            return discord.Value.UseDevLogin
                ? new DevDiscordClient(provider.GetRequiredService<IOptions<BackendOptions>>().Value.PublicUrl.TrimEnd('/'))
                : new DiscordClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DiscordClient)), discord);
        });

        services.AddSingleton(provider => LoadSigningKey(config, isDevelopment, provider.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(provider => new JoinTicketIssuer(
            provider.GetRequiredService<JoinTicketSigningKey>(),
            provider.GetRequiredService<IOptions<BackendOptions>>().Value.PublicUrl.TrimEnd('/'),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new PublicKeySet(provider.GetRequiredService<JoinTicketSigningKey>().ExportPublicKeySet()));

        services.AddAuthentication(SessionAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthentication>(SessionAuthentication.SchemeName, null);
        services.AddAuthorization();
        services.AddProblemDetails();

        // Pro IP höchstens 30 Login-Anfragen pro Minute. Ein normaler Login braucht drei.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unbekannt",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
        });

        // Coolify setzt einen Proxy (Traefik) davor. Er liefert die echte IP des Spielers in X-Forwarded-For.
        // Der Container ist nur über diesen Proxy erreichbar, deshalb wird jedem Absender vertraut.
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });
    }

    public static async Task UseBackendAsync(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<BackendOptions>>().Value.MigrateOnStartup)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BackendDb>().Database.MigrateAsync();
        }

        // Schlüssel beim Start laden, damit ein kaputter Schlüssel sofort auffällt und nicht erst beim ersten Ticket.
        app.Services.GetRequiredService<JoinTicketSigningKey>();

        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(ApiRoutes.Health, async (BackendDb db, CancellationToken cancellationToken) =>
            await db.Database.CanConnectAsync(cancellationToken) ? Results.Text("ok") : Results.StatusCode(503));
        app.MapLogin();
        app.MapAccount();
    }

    private static JoinTicketSigningKey LoadSigningKey(IConfiguration config, bool isDevelopment, ILoggerFactory loggers)
    {
        var configured = config["JoinTickets:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
            return JoinTicketSigningKey.Import(configured);

        if (!isDevelopment)
            throw new InvalidOperationException("JoinTickets:SigningKey fehlt. Erzeugen mit: dotnet AoS2.Backend.dll generate-signing-key");

        loggers.CreateLogger(typeof(BackendSetup)).LogWarning(
            "JoinTickets:SigningKey fehlt, nutze einen zufälligen Schlüssel. Join-Tickets gelten nur bis zum nächsten Neustart.");
        return JoinTicketSigningKey.Generate();
    }
}
