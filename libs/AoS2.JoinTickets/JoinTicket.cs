using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AoS2.JoinTickets;

/// <summary>Was ein gültiges Join-Ticket über den Spieler aussagt.</summary>
/// <param name="TicketId">Eindeutig je Ticket. Ein Spielserver kann sich benutzte IDs merken, damit jedes Ticket nur einmal gilt.</param>
public sealed record JoinTicket(Guid AccountId, string DisplayName, string Server, string TicketId, DateTimeOffset ExpiresAt);

/// <summary>Stellt Join-Tickets aus (Backend).</summary>
/// <param name="issuer">Adresse des Backends, steht als <c>iss</c> im Ticket.</param>
public sealed class JoinTicketIssuer(JoinTicketSigningKey key, string issuer, TimeProvider time)
{
    /// <summary>So lange gilt ein Ticket. Der Client holt es direkt vor dem Verbinden.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly SigningCredentials _credentials = new(key.SecurityKey, JoinTicketSigningKey.Algorithm);

    public (string Ticket, DateTimeOffset ExpiresAt) Issue(Guid accountId, string displayName, string server)
    {
        // Sekundengenau, wie es im JWT steht.
        var now = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().ToUnixTimeSeconds());
        var expires = now + Lifetime;
        var ticket = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = NormalizeServer(server),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, displayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        });
        return (ticket, expires);
    }

    /// <summary>Groß-/Kleinschreibung und Leerzeichen sollen nicht über die Gültigkeit entscheiden.</summary>
    public static string NormalizeServer(string server) => server.Trim().ToLowerInvariant();
}

/// <summary>Prüft Join-Tickets (Spielserver). Braucht nur den öffentlichen Schlüssel des Backends.</summary>
/// <param name="publicKeySet">JSON Web Key Set vom Backend (<c>/.well-known/jwks.json</c>).</param>
/// <param name="issuer">Adresse des Backends, dem der Server vertraut.</param>
/// <param name="server">Name dieses Spielservers, so wie Clients ihn beim Ticket angeben.</param>
public sealed class JoinTicketValidator(string publicKeySet, string issuer, string server, TimeProvider time)
{
    /// <summary>Uhren von Backend und Spielserver gehen nie ganz gleich.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _parameters = new()
    {
        ValidIssuer = issuer,
        ValidAudience = JoinTicketIssuer.NormalizeServer(server),
        IssuerSigningKeys = new JsonWebKeySet(publicKeySet).GetSigningKeys(),
        ValidAlgorithms = [JoinTicketSigningKey.Algorithm],
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return notBefore is { } nbf && expires is { } exp && now >= nbf - ClockSkew && now <= exp + ClockSkew;
        },
    };

    /// <summary>Gibt das Ticket zurück, wenn es echt, für diesen Server und noch gültig ist, sonst <c>null</c>.</summary>
    public async Task<JoinTicket?> ValidateAsync(string ticket)
    {
        var result = await _handler.ValidateTokenAsync(ticket, _parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
            return null;

        if (!Guid.TryParse(token.Subject, out var accountId)
            || !token.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Name, out var name)
            || string.IsNullOrEmpty(token.Id))
            return null;

        return new JoinTicket(accountId, name, token.Audiences.Single(), token.Id, new DateTimeOffset(token.ValidTo, TimeSpan.Zero));
    }
}
