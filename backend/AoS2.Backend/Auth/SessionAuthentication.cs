using System.Security.Claims;
using System.Text.Encodings.Web;
using AoS2.Backend.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Auth;

/// <summary>
/// Prüft das Sitzungstoken aus <c>Authorization: Bearer …</c>. Endpunkte mit
/// <c>RequireAuthorization()</c> bekommen über <see cref="GetSession"/> die Sitzung samt Konto.
/// </summary>
internal sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BackendDb db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    /// <summary>
    /// Eine Sitzung läuft ab, wenn sie so lange nicht benutzt wurde. Wer regelmäßig spielt, bleibt angemeldet.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Damit nicht jede Anfrage in die Datenbank schreibt, wird die Laufzeit höchstens so oft verlängert.</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromHours(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var hash = SecretTokens.Hash(header["Bearer ".Length..].Trim());
        var now = time.GetUtcNow().UtcDateTime;
        var session = await db.Sessions.Include(s => s.Account)
            .SingleOrDefaultAsync(s => s.TokenHash == hash && s.ExpiresAt > now, Context.RequestAborted);
        if (session is null)
            return AuthenticateResult.Fail("Sitzung ungültig oder abgelaufen.");

        if (now - session.LastUsedAt >= RenewInterval)
        {
            session.LastUsedAt = now;
            session.ExpiresAt = now + Lifetime;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        Context.Features.Set(session);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

internal static class SessionHttpContextExtensions
{
    /// <summary>Die Sitzung der Anfrage. Nur in Endpunkten mit <c>RequireAuthorization()</c>.</summary>
    public static Session GetSession(this HttpContext http) =>
        http.Features.Get<Session>() ?? throw new InvalidOperationException("Endpunkt ohne RequireAuthorization().");
}
