using AoS2.Backend.Auth;
using AoS2.Backend.Data;
using AoS2.Contracts;
using AoS2.JoinTickets;

namespace AoS2.Backend;

internal static class AccountEndpoints
{
    public const int MaxServerLength = 255;

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Me, (HttpContext http) => ToInfo(http.GetSession().Account)).RequireAuthorization();

        app.MapPost(ApiRoutes.JoinTickets, (JoinTicketRequest request, HttpContext http, JoinTicketIssuer issuer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Server) || request.Server.Length > MaxServerLength)
                return Results.Problem(statusCode: 400, title: "Ungültiger Server.");

            var account = http.GetSession().Account;
            var (ticket, expiresAt) = issuer.Issue(account.Id, account.DisplayName, request.Server);
            return Results.Ok(new JoinTicketResponse { Ticket = ticket, ExpiresAt = expiresAt });
        }).RequireAuthorization();

        // Öffentlich: Spielserver holen sich hier den Schlüssel zum Prüfen der Tickets.
        app.MapGet(ApiRoutes.JoinTicketKeys, (HttpContext http, PublicKeySet keys) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Content(keys.Json, "application/json");
        });
    }

    public static AccountInfo ToInfo(Account account) => new()
    {
        Id = account.Id,
        DisplayName = account.DisplayName,
        AvatarUrl = account.AvatarHash is null ? null : $"https://cdn.discordapp.com/avatars/{account.DiscordId}/{account.AvatarHash}.png?size=128",
    };
}

/// <summary>Der öffentliche Join-Ticket-Schlüssel als JSON, einmal beim Start erzeugt.</summary>
internal sealed record PublicKeySet(string Json);
