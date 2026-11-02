using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Auth;

public sealed class DiscordOptions
{
    public const string Section = "Discord";

    /// <summary>„Client ID“ aus dem Discord Developer Portal (OAuth2).</summary>
    public string? ClientId { get; set; }

    /// <summary>„Client Secret“ aus dem Developer Portal. Nur über Umgebungsvariable oder user-secrets setzen.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Nur zum Entwickeln: statt Discord eine eigene Seite, auf der man einen beliebigen Namen eingibt.
    /// Das Backend verweigert den Start, wenn das außerhalb der Development-Umgebung eingeschaltet ist.
    /// </summary>
    public bool UseDevLogin { get; set; }
}

/// <summary>Was das Backend von Discord über einen Spieler erfährt (Scope <c>identify</c>).</summary>
/// <param name="GlobalName">Anzeigename, den man in Discord selbst wählt. Kann fehlen.</param>
/// <param name="Avatar">Kennung des Profilbilds, <c>null</c> ohne eigenes Bild.</param>
public sealed record DiscordUser(string Id, string Username, string? GlobalName, string? Avatar)
{
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName;
}

/// <summary>Discord hat nicht oder unerwartet geantwortet.</summary>
public sealed class DiscordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Der OAuth2-Teil von Discord. In Tests durch eine Attrappe ersetzt.</summary>
public interface IDiscordClient
{
    /// <summary>Adresse der Discord-Seite „Anwendung autorisieren“.</summary>
    string GetAuthorizeUrl(string state, string redirectUri);

    /// <summary>Tauscht den Code aus der Weiterleitung gegen ein Discord-Zugriffstoken (braucht das Client-Secret).</summary>
    Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    Task<DiscordUser> GetUserAsync(string accessToken, CancellationToken cancellationToken);
}

/// <summary>Spricht mit der echten Discord-API.</summary>
public sealed class DiscordClient(HttpClient http, IOptions<DiscordOptions> options) : IDiscordClient
{
    public const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
    public static readonly Uri ApiBase = new("https://discord.com/api/v10/");

    public string GetAuthorizeUrl(string state, string redirectUri) => QueryHelpers.AddQueryString(AuthorizeUrl, new Dictionary<string, string?>
    {
        ["response_type"] = "code",
        ["client_id"] = options.Value.ClientId,
        ["scope"] = "identify",
        ["state"] = state,
        ["redirect_uri"] = redirectUri,
        // Wer schon einmal zugestimmt hat, muss es nicht jedes Mal wieder tun.
        ["prompt"] = "none",
    });

    public async Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, "oauth2/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }),
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.ClientId}:{options.Value.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var token = await SendAsync<TokenResponse>(request, cancellationToken);
        return string.IsNullOrEmpty(token.AccessToken) ? throw new DiscordException("Discord hat kein Zugriffstoken geliefert.") : token.AccessToken;
    }

    public async Task<DiscordUser> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, "users/@me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var user = await SendAsync<UserResponse>(request, cancellationToken);
        if (string.IsNullOrEmpty(user.Id) || string.IsNullOrEmpty(user.Username))
            throw new DiscordException("Discord hat keinen Benutzer geliefert.");
        return new DiscordUser(user.Id, user.Username, user.GlobalName, user.Avatar);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new DiscordException($"Discord antwortet mit {(int)response.StatusCode} auf {request.RequestUri?.AbsolutePath}.");
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new DiscordException("Discord hat eine leere Antwort geschickt.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DiscordException("Discord ist nicht erreichbar oder antwortet unverständlich.", ex);
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record UserResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("global_name")] string? GlobalName,
        [property: JsonPropertyName("avatar")] string? Avatar);
}

/// <summary>
/// Entwicklungs-Login ohne Discord (<see cref="DiscordOptions.UseDevLogin"/>): Die „Discord-Seite“ ist
/// <c>/dev/discord/authorize</c> im Backend selbst, und der Code enthält einfach den eingegebenen Namen.
/// </summary>
public sealed class DevDiscordClient(string publicUrl) : IDiscordClient
{
    public const string AuthorizePath = "/dev/discord/authorize";
    public const string CodePrefix = "dev:";

    public string GetAuthorizeUrl(string state, string redirectUri) =>
        QueryHelpers.AddQueryString(publicUrl + AuthorizePath, new Dictionary<string, string?> { ["state"] = state });

    public static string CreateCode(string name) => CodePrefix + WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(name));

    public Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
        code.StartsWith(CodePrefix, StringComparison.Ordinal)
            ? Task.FromResult(code)
            : Task.FromException<string>(new DiscordException("Unbekannter Entwicklungs-Code."));

    public Task<DiscordUser> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var name = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(accessToken[CodePrefix.Length..]));
        // Gleicher Name → gleiches Konto, wie bei Discord die ID.
        var id = "dev-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16];
        return Task.FromResult(new DiscordUser(id, name, null, null));
    }
}
