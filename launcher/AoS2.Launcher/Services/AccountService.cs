using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AoS2.Contracts;

namespace AoS2.Launcher.Services;

public enum SessionStatus
{
    SignedOut,
    SignedIn,

    /// <summary>Gespeicherte Anmeldung vorhanden, aber das Backend war nicht erreichbar. Spielen geht trotzdem.</summary>
    Offline,
}

/// <param name="DisplayName">Name zum Anzeigen; <c>null</c> bei <see cref="SessionStatus.SignedOut"/>.</param>
public sealed record SessionCheck(SessionStatus Status, string? DisplayName);

/// <summary>Anmeldung fehlgeschlagen. <see cref="Exception.Message"/> ist für Spieler gedacht.</summary>
public sealed class SignInException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Discord-Login über das Backend. Für das ViewModel austauschbar.</summary>
public interface IAccountService
{
    /// <summary>Prüft die gespeicherte Anmeldung beim Backend.</summary>
    Task<SessionCheck> RestoreAsync(CancellationToken cancellationToken);

    /// <summary>Öffnet den Browser und wartet, bis die Anmeldung fertig ist.</summary>
    /// <exception cref="SignInException">Mit einer Meldung für den Spieler.</exception>
    Task<AccountInfo> SignInAsync(CancellationToken cancellationToken);

    /// <summary>Öffnet die Anmeldeseite noch einmal (während <see cref="SignInAsync"/> läuft).</summary>
    void ReopenBrowser();

    Task SignOutAsync();

    /// <summary>Umgebungsvariablen für den Spielprozess: Sitzungstoken und Website-Adresse.</summary>
    IReadOnlyDictionary<string, string> GetGameEnvironment();
}

/// <summary>
/// Ablauf (docs/backend.md, Abschnitt 2): Port auf 127.0.0.1 öffnen → Browser mit der Login-Seite der Website
/// → Discord → Website leitet mit Einmal-Code an den Port weiter → Code + PKCE-Verifier gegen Sitzungstoken tauschen
/// → Token verschlüsselt speichern.
/// </summary>
public sealed class AccountService(HttpClient http, string siteUrl, SessionStore store, Action<string> openBrowser) : IAccountService
{
    private SavedSession? _session;
    private string? _loginUrl;

    public async Task<SessionCheck> RestoreAsync(CancellationToken cancellationToken)
    {
        var saved = store.Load();
        if (saved is null || !string.Equals(saved.SiteUrl, siteUrl, StringComparison.OrdinalIgnoreCase))
        {
            // Ein Token von einem anderen Backend (z. B. Test-Server) gilt hier nicht.
            store.Delete();
            return new SessionCheck(SessionStatus.SignedOut, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, siteUrl + ApiRoutes.Me);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                store.Delete();
                return new SessionCheck(SessionStatus.SignedOut, null);
            }

            if (response.IsSuccessStatusCode
                && await response.Content.ReadFromJsonAsync(BackendJsonContext.Default.AccountInfo, cancellationToken) is { } account)
            {
                // Name kann sich in Discord geändert haben.
                if (account.DisplayName != saved.DisplayName)
                {
                    saved.DisplayName = account.DisplayName;
                    store.Save(saved);
                }
                _session = saved;
                return new SessionCheck(SessionStatus.SignedIn, account.DisplayName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Backend nicht erreichbar: gespeicherte Anmeldung gilt weiter.
        }

        _session = saved;
        return new SessionCheck(SessionStatus.Offline, saved.DisplayName);
    }

    public async Task<AccountInfo> SignInAsync(CancellationToken cancellationToken)
    {
        await EnsureBackendReachableAsync(cancellationToken);

        // PKCE: Nur dieser Launcher kennt den Verifier. Wer den Einmal-Code abfängt, kann ihn ohne Verifier nicht nutzen.
        var verifier = CreateRandom();
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = CreateRandom();

        using var listener = new LoopbackListener(siteUrl);
        _loginUrl = $"{siteUrl}{SiteRoutes.Login}?{LoginParameters.Port}={listener.Port}" +
                    $"&{LoginParameters.State}={state}&{LoginParameters.CodeChallenge}={challenge}";
        try
        {
            openBrowser(_loginUrl);
            var result = await listener.WaitAsync(state, cancellationToken);
            if (result.Code is null)
            {
                throw new SignInException(result.Error == LoginErrors.AccessDenied
                    ? "Du hast die Anmeldung bei Discord abgebrochen."
                    : "Discord hat nicht richtig geantwortet. Versuch es gleich noch einmal.");
            }

            var session = await ExchangeAsync(result.Code, verifier, cancellationToken);
            _session = new SavedSession
            {
                Token = session.Token,
                SiteUrl = siteUrl,
                AccountId = session.Account.Id,
                DisplayName = session.Account.DisplayName,
            };
            store.Save(_session);
            return session.Account;
        }
        finally
        {
            _loginUrl = null;
        }
    }

    public void ReopenBrowser()
    {
        if (_loginUrl is { } url)
            openBrowser(url);
    }

    public async Task SignOutAsync()
    {
        var token = _session?.Token ?? store.Load()?.Token;
        _session = null;
        store.Delete();
        if (token is null)
            return;

        // Sitzung auch im Backend beenden. Klappt das nicht, läuft sie dort eben irgendwann ab.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, siteUrl + ApiRoutes.Logout);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    public IReadOnlyDictionary<string, string> GetGameEnvironment() => _session is null
        ? new Dictionary<string, string>()
        : new Dictionary<string, string>
        {
            [LaunchEnvironment.SessionToken] = _session.Token,
            [LaunchEnvironment.SiteUrl] = siteUrl,
        };

    /// <summary>Lieber gleich melden, als den Spieler auf eine Fehlerseite im Browser zu schicken.</summary>
    private async Task EnsureBackendReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(siteUrl + ApiRoutes.Health, cancellationToken);
            if (response.IsSuccessStatusCode)
                return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SignInException("Der Anmeldeserver antwortet nicht. Prüf deine Internetverbindung.", ex);
        }
        throw new SignInException("Der Anmeldeserver hat gerade ein Problem. Versuch es später noch einmal.");
    }

    private async Task<SessionResponse> ExchangeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        try
        {
            var request = new SessionRequest { Code = code, CodeVerifier = verifier };
            using var response = await http.PostAsJsonAsync(siteUrl + ApiRoutes.Session, request, BackendJsonContext.Default.SessionRequest, cancellationToken);
            if (response.IsSuccessStatusCode
                && await response.Content.ReadFromJsonAsync(BackendJsonContext.Default.SessionResponse, cancellationToken) is { Token.Length: > 0, Account: not null } session)
            {
                return session;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SignInException("Der Anmeldeserver antwortet nicht. Prüf deine Internetverbindung.", ex);
        }
        throw new SignInException("Die Anmeldung ist abgelaufen. Bitte noch einmal versuchen.");
    }

    private static string CreateRandom() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>JSON wie das Backend es schreibt (camelCase).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(AccountInfo))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;
