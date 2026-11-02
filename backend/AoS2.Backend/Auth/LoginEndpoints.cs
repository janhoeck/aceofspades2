using System.Security.Cryptography;
using System.Text;
using AoS2.Backend.Data;
using AoS2.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Auth;

/// <summary>
/// Discord-Login für den Launcher (Ablauf: docs/backend.md, Abschnitt 2). Die Seiten im Browser zeigt die
/// Website (<c>web/</c>); sie ruft dafür die internen Endpunkte unter <c>/api/internal</c> auf.
/// <list type="number">
/// <item>Launcher öffnet im Browser <c>/login?port=…&amp;state=…&amp;code_challenge=…</c> auf der Website.</item>
/// <item>Website → <c>POST /api/internal/login/start</c>: Backend merkt sich die Anmeldung und liefert die Discord-Adresse.
///       Die Website setzt ein Cookie und leitet zu Discord weiter.</item>
/// <item>Discord leitet zur Website zurück. Website prüft das Cookie → <c>POST /api/internal/login/complete</c>:
///       Backend tauscht den Code bei Discord (mit Client-Secret), legt das Konto an und liefert die Adresse,
///       unter der der Launcher (<c>127.0.0.1:port</c>) den Einmal-Code bekommt.</item>
/// <item>Launcher tauscht Einmal-Code + <c>code_verifier</c> bei <c>/api/auth/session</c> gegen ein Sitzungstoken.</item>
/// </list>
/// </summary>
internal static class LoginEndpoints
{
    public const string InternalKeyHeader = "X-Internal-Key";
    public const string StartPath = "/api/internal/login/start";
    public const string CompletePath = "/api/internal/login/complete";

    /// <summary>So lange hat man Zeit, sich bei Discord anzumelden.</summary>
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    /// <summary>So lange gilt der Einmal-Code. Der Launcher tauscht ihn sofort ein.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        // Nur für die Website. Ohne den gemeinsamen Schlüssel könnte jemand die Cookie-Prüfung der Website umgehen.
        var website = app.MapGroup("")
            .AddEndpointFilter(async (context, next) =>
                IsWebsite(context.HttpContext) ? await next(context) : Results.StatusCode(StatusCodes.Status403Forbidden))
            .RequireRateLimiting(RateLimits.Login);
        website.MapPost(StartPath, StartAsync);
        website.MapPost(CompletePath, CompleteAsync);

        app.MapPost(ApiRoutes.Session, ExchangeAsync).RequireRateLimiting(RateLimits.Login);
        app.MapPost(ApiRoutes.Logout, LogoutAsync).RequireAuthorization();
    }

    private static bool IsWebsite(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IOptions<BackendOptions>>().Value.InternalKey;
        string? actual = http.Request.Headers[InternalKeyHeader];
        return actual is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    }

    // ---------------------------------------------------------------- 1. Start

    private static async Task<IResult> StartAsync(
        LoginStartRequest request,
        BackendDb db,
        IDiscordClient discord,
        TimeProvider time,
        IOptions<BackendOptions> options,
        CancellationToken cancellationToken)
    {
        if (request.Port is < 1024 or > 65535
            || !SecretTokens.IsUrlSafe(request.State, 16, 128)
            || !SecretTokens.IsUrlSafe(request.CodeChallenge, 43, 43))
        {
            return Results.Problem(statusCode: 400, title: "Ungültige Anmeldung.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await db.LoginAttempts.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        await db.Sessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);

        var attempt = new LoginAttempt
        {
            Id = SecretTokens.Create(),
            Port = request.Port,
            LauncherState = request.State,
            CodeChallenge = request.CodeChallenge,
            ExpiresAt = now + AttemptLifetime,
        };
        db.LoginAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new LoginStartResponse(attempt.Id, discord.GetAuthorizeUrl(attempt.Id, options.Value.CallbackUrl), (int)AttemptLifetime.TotalSeconds));
    }

    // ---------------------------------------------------------------- 2. Rückkehr von Discord

    private static async Task<IResult> CompleteAsync(
        LoginCompleteRequest request,
        BackendDb db,
        IDiscordClient discord,
        TimeProvider time,
        IOptions<BackendOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var attempt = string.IsNullOrEmpty(request.AttemptId)
            ? null
            : await db.LoginAttempts.SingleOrDefaultAsync(a => a.Id == request.AttemptId && a.ExpiresAt > now && a.AccountId == null, cancellationToken);
        if (attempt is null)
            return Results.Problem(statusCode: 404, title: "Anmeldung abgelaufen.");

        if (request.Error is not null || string.IsNullOrEmpty(request.Code))
            return await FailAsync(LoginErrors.AccessDenied);

        DiscordUser user;
        try
        {
            var accessToken = await discord.ExchangeCodeAsync(request.Code, options.Value.CallbackUrl, cancellationToken);
            user = await discord.GetUserAsync(accessToken, cancellationToken);
        }
        catch (DiscordException ex)
        {
            loggers.CreateLogger(typeof(LoginEndpoints)).LogWarning(ex, "Discord-Login fehlgeschlagen");
            return await FailAsync(LoginErrors.DiscordFailed);
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.DiscordId == user.Id, cancellationToken);
        if (account is null)
        {
            account = new Account
            {
                Id = Guid.CreateVersion7(),
                DiscordId = user.Id,
                DiscordUsername = user.Username,
                DisplayName = user.DisplayName,
                CreatedAt = now,
            };
            db.Accounts.Add(account);
        }
        account.DiscordUsername = Truncate(user.Username, 64);
        account.DisplayName = Truncate(user.DisplayName, 64);
        account.AvatarHash = user.Avatar;
        account.LastLoginAt = now;

        var exchangeCode = SecretTokens.Create();
        attempt.AccountId = account.Id;
        attempt.ExchangeCodeHash = SecretTokens.Hash(exchangeCode);
        attempt.ExpiresAt = now + CodeLifetime;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new LoginCompleteResponse(LoopbackUrl(attempt, LoginParameters.Code, exchangeCode)));

        async Task<IResult> FailAsync(string reason)
        {
            db.LoginAttempts.Remove(attempt);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new LoginCompleteResponse(LoopbackUrl(attempt, LoginParameters.Error, reason)));
        }
    }

    /// <summary>Adresse, unter der der Launcher auf dem Rechner des Spielers wartet.</summary>
    private static string LoopbackUrl(LoginAttempt attempt, string key, string value) =>
        QueryHelpers.AddQueryString($"http://127.0.0.1:{attempt.Port}{LoginParameters.LoopbackPath}", new Dictionary<string, string?>
        {
            [key] = value,
            [LoginParameters.State] = attempt.LauncherState,
        });

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    // ---------------------------------------------------------------- 3. Einmal-Code gegen Sitzung tauschen

    private static async Task<IResult> ExchangeAsync(SessionRequest request, BackendDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
            return InvalidCode();

        var now = time.GetUtcNow().UtcDateTime;
        var hash = SecretTokens.Hash(request.Code);
        var attempt = await db.LoginAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.ExchangeCodeHash == hash && a.ExpiresAt > now, cancellationToken);
        if (attempt?.AccountId is not { } accountId)
            return InvalidCode();

        // Der Code gilt genau einmal, auch bei falschem Verifier. Löscht eine parallele Anfrage ihn zuerst, gilt er hier nicht mehr.
        if (await db.LoginAttempts.Where(a => a.Id == attempt.Id).ExecuteDeleteAsync(cancellationToken) == 0
            || !SecretTokens.MatchesChallenge(request.CodeVerifier, attempt.CodeChallenge))
        {
            return InvalidCode();
        }

        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, cancellationToken);
        var token = SecretTokens.CreateSessionToken();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            AccountId = account.Id,
            TokenHash = SecretTokens.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + SessionAuthentication.Lifetime,
            LastUsedAt = now,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new SessionResponse
        {
            Token = token,
            ExpiresAt = new DateTimeOffset(session.ExpiresAt, TimeSpan.Zero),
            Account = AccountEndpoints.ToInfo(account),
        });
    }

    private static IResult InvalidCode() =>
        Results.Problem(statusCode: 400, title: "Der Anmeldecode ist ungültig oder abgelaufen.");

    // ---------------------------------------------------------------- Abmelden

    private static async Task<IResult> LogoutAsync(HttpContext http, BackendDb db, CancellationToken cancellationToken)
    {
        var sessionId = http.GetSession().Id;
        await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>Website → Backend: Anmeldung des Launchers beginnen.</summary>
internal sealed record LoginStartRequest(int Port, string State, string CodeChallenge);

/// <param name="AttemptId">Geht als <c>state</c> an Discord; die Website legt ihn zusätzlich als Cookie ab.</param>
/// <param name="AuthorizeUrl">Discord-Seite „Anwendung autorisieren“ (beim Entwicklungs-Login eine Seite der Website).</param>
/// <param name="MaxAgeSeconds">So lange gilt die Anmeldung, und so lange soll das Cookie leben.</param>
internal sealed record LoginStartResponse(string AttemptId, string AuthorizeUrl, int MaxAgeSeconds);

/// <summary>Website → Backend: Discord ist zurück (mit <paramref name="Code"/> oder <paramref name="Error"/>).</summary>
internal sealed record LoginCompleteRequest(string AttemptId, string? Code, string? Error);

/// <param name="RedirectUrl">Wohin die Website den Browser schickt: zum Launcher auf 127.0.0.1, mit Einmal-Code oder Fehler.</param>
internal sealed record LoginCompleteResponse(string RedirectUrl);
