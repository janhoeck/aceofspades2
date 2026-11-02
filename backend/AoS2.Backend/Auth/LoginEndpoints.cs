using AoS2.Backend.Data;
using AoS2.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Auth;

/// <summary>
/// Discord-Login für den Launcher (Ablauf: docs/architecture.md, Abschnitt 6).
/// <list type="number">
/// <item>Launcher öffnet im Browser <c>/auth/discord/login?port=…&amp;state=…&amp;code_challenge=…</c>.</item>
/// <item>Backend merkt sich die Anmeldung, setzt ein Cookie und leitet zu Discord weiter.</item>
/// <item>Discord leitet zurück auf <c>/auth/discord/callback</c>. Backend holt den Spieler bei Discord,
///       legt das Konto an und leitet mit einem Einmal-Code zum Launcher (<c>127.0.0.1:port</c>) weiter.</item>
/// <item>Launcher tauscht Einmal-Code + <c>code_verifier</c> bei <c>/auth/session</c> gegen ein Sitzungstoken.</item>
/// </list>
/// </summary>
internal static class LoginEndpoints
{
    public const string CookieName = "aos2_login";
    public const string DevApprovePath = "/dev/discord/approve";

    /// <summary>So lange hat man Zeit, sich bei Discord anzumelden.</summary>
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    /// <summary>So lange gilt der Einmal-Code. Der Launcher tauscht ihn sofort ein.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    private const string CookiePath = "/auth/discord";

    public static void MapLogin(this IEndpointRouteBuilder app, bool devLogin)
    {
        app.MapGet(ApiRoutes.DiscordLogin, StartAsync).RequireRateLimiting(RateLimits.Login);
        app.MapGet(ApiRoutes.DiscordCallback, CallbackAsync).RequireRateLimiting(RateLimits.Login);
        app.MapPost(ApiRoutes.Session, ExchangeAsync).RequireRateLimiting(RateLimits.Login);
        app.MapPost(ApiRoutes.Logout, LogoutAsync).RequireAuthorization();

        if (devLogin)
        {
            app.MapGet(DevDiscordClient.AuthorizePath, (string? state) =>
                state is null ? Results.BadRequest() : LoginPages.DevAuthorize(state));
            app.MapGet(DevApprovePath, (string? state, string? name, string? deny, IOptions<BackendOptions> options) =>
            {
                var query = deny is not null || string.IsNullOrWhiteSpace(name)
                    ? new Dictionary<string, string?> { [LoginParameters.Error] = LoginErrors.AccessDenied, ["state"] = state }
                    : new Dictionary<string, string?> { ["code"] = DevDiscordClient.CreateCode(name.Trim()), ["state"] = state };
                return Results.Redirect(QueryHelpers.AddQueryString(options.Value.CallbackUrl, query));
            });
        }
    }

    // ---------------------------------------------------------------- 1. Start

    private static async Task<IResult> StartAsync(
        HttpContext http,
        int? port,
        string? state,
        [FromQuery(Name = LoginParameters.CodeChallenge)] string? codeChallenge,
        BackendDb db,
        IDiscordClient discord,
        TimeProvider time,
        IOptions<BackendOptions> options,
        CancellationToken cancellationToken)
    {
        if (port is not (>= 1024 and <= 65535)
            || !SecretTokens.IsUrlSafe(state, 16, 128)
            || !SecretTokens.IsUrlSafe(codeChallenge, 43, 43))
        {
            return LoginPages.Error(400, "Ungültige Anmeldung", "Dieser Link stammt nicht vom Launcher. Starte die Anmeldung bitte im Launcher.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        await db.LoginAttempts.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        await db.Sessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);

        var attempt = new LoginAttempt
        {
            Id = SecretTokens.Create(),
            Port = port.Value,
            LauncherState = state!,
            CodeChallenge = codeChallenge!,
            ExpiresAt = now + AttemptLifetime,
        };
        db.LoginAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        // Bindet die Anmeldung an diesen Browser. Ohne das könnte jemand einem Spieler einen fremden
        // Discord-Rückkehr-Link unterschieben, und der Launcher wäre plötzlich mit dem fremden Konto angemeldet.
        http.Response.Cookies.Append(CookieName, attempt.Id, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.Value.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = CookiePath,
            MaxAge = AttemptLifetime,
        });

        return Results.Redirect(discord.GetAuthorizeUrl(attempt.Id, options.Value.CallbackUrl));
    }

    // ---------------------------------------------------------------- 2. Rückkehr von Discord

    private static async Task<IResult> CallbackAsync(
        HttpContext http,
        string? code,
        string? state,
        string? error,
        BackendDb db,
        IDiscordClient discord,
        TimeProvider time,
        IOptions<BackendOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var attempt = state is null
            ? null
            : await db.LoginAttempts.SingleOrDefaultAsync(a => a.Id == state && a.ExpiresAt > now && a.AccountId == null, cancellationToken);

        if (attempt is null || http.Request.Cookies[CookieName] != attempt.Id)
        {
            return LoginPages.Error(400, "Anmeldung abgelaufen",
                "Diese Anmeldung ist abgelaufen oder wurde in einem anderen Browser gestartet. Starte sie bitte im Launcher neu.");
        }

        http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath, Secure = options.Value.IsHttps, HttpOnly = true });

        if (error is not null || string.IsNullOrEmpty(code))
            return await FailAsync(LoginErrors.AccessDenied);

        DiscordUser user;
        try
        {
            var accessToken = await discord.ExchangeCodeAsync(code, options.Value.CallbackUrl, cancellationToken);
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

        return Results.Redirect(LoopbackUrl(attempt, LoginParameters.Code, exchangeCode));

        async Task<IResult> FailAsync(string reason)
        {
            db.LoginAttempts.Remove(attempt);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Redirect(LoopbackUrl(attempt, LoginParameters.Error, reason));
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
