using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AoS2.Contracts;
using AoS2.Launcher.Services;
using AoS2.Updates.Tests;

namespace AoS2.Launcher.Tests;

/// <summary>
/// Der Login-Ablauf des Launchers mit echtem Loopback-Port, nachgebildetem Browser und nachgebildetem Backend.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private const string Backend = "https://api.test";

    private readonly TempDirectory _temp = new();
    private readonly FakeBackend _backend = new();
    private readonly SessionStore _store;
    private readonly List<string> _openedUrls = [];

    /// <summary>Was der „Browser“ am Ende an den Launcher weitergibt; <c>null</c> = Code vom Backend.</summary>
    private string? _browserError;

    public AccountServiceTests() => _store = new SessionStore(_temp.Combine("session.dat"), new PlainProtector());

    public void Dispose() => _temp.Dispose();

    private AccountService CreateService(string backendUrl = Backend) =>
        new(new HttpClient(_backend), backendUrl, _store, OpenBrowser);

    /// <summary>Der Browser ruft die Login-Seite auf; das Backend (nach Discord) schickt ihn zurück zum Launcher.</summary>
    private void OpenBrowser(string url)
    {
        _openedUrls.Add(url);
        var query = LoopbackListener.ParseQuery(new Uri(url).Query.TrimStart('?'));
        _backend.Challenge = query[LoginParameters.CodeChallenge];
        var loopback = $"http://127.0.0.1:{query[LoginParameters.Port]}{LoginParameters.LoopbackPath}?" +
                       (_browserError is null ? $"code={FakeBackend.Code}" : $"error={_browserError}") +
                       $"&state={query[LoginParameters.State]}";
        _ = Task.Run(async () =>
        {
            using var browser = new HttpClient();
            using var _ = await browser.GetAsync(loopback);
        });
    }

    // ---------------------------------------------------------------- Anmelden

    [Fact]
    public async Task SignIn_OpensBackendLogin_WithPkce()
    {
        var account = await CreateService().SignInAsync(default);

        Assert.Equal("Jan", account.DisplayName);
        var url = Assert.Single(_openedUrls);
        Assert.StartsWith($"{Backend}{ApiRoutes.DiscordLogin}?port=", url);
        // Der Verifier verlässt den Launcher erst beim Eintauschen und passt zur Challenge.
        Assert.DoesNotContain(_backend.Verifier!, url);
        Assert.True(_backend.VerifierMatched);
    }

    [Fact]
    public async Task SignIn_SavesSession_AndPassesItToGame()
    {
        var service = CreateService();

        await service.SignInAsync(default);

        var saved = _store.Load();
        Assert.Equal(FakeBackend.Token, saved!.Token);
        Assert.Equal(Backend, saved.BackendUrl);
        Assert.Equal("Jan", saved.DisplayName);
        Assert.Equal(new Dictionary<string, string>
        {
            [LaunchEnvironment.SessionToken] = FakeBackend.Token,
            [LaunchEnvironment.BackendUrl] = Backend,
        }, service.GetGameEnvironment());
    }

    [Theory]
    [InlineData(LoginErrors.AccessDenied, "abgebrochen")]
    [InlineData(LoginErrors.DiscordFailed, "Discord hat nicht")]
    public async Task SignIn_ErrorFromBrowser_ExplainsWhy(string error, string message)
    {
        _browserError = error;

        var ex = await Assert.ThrowsAsync<SignInException>(() => CreateService().SignInAsync(default));

        Assert.Contains(message, ex.Message);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task SignIn_BackendDown_FailsBeforeOpeningBrowser()
    {
        _backend.Down = true;

        var ex = await Assert.ThrowsAsync<SignInException>(() => CreateService().SignInAsync(default));

        Assert.Contains("antwortet nicht", ex.Message);
        Assert.Empty(_openedUrls);
    }

    [Fact]
    public async Task SignIn_CodeRejected_Fails()
    {
        _backend.RejectCode = true;

        var ex = await Assert.ThrowsAsync<SignInException>(() => CreateService().SignInAsync(default));

        Assert.Contains("abgelaufen", ex.Message);
    }

    [Fact]
    public async Task SignIn_Cancel_StopsWaiting()
    {
        var service = new AccountService(new HttpClient(_backend), Backend, _store, _openedUrls.Add);
        using var cancellation = new CancellationTokenSource();

        var signIn = service.SignInAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReopenBrowser_OpensSameLoginAgain()
    {
        var service = new AccountService(new HttpClient(_backend), Backend, _store, _openedUrls.Add);
        using var cancellation = new CancellationTokenSource();
        var signIn = service.SignInAsync(cancellation.Token);
        while (_openedUrls.Count == 0)
            await Task.Delay(10);

        service.ReopenBrowser();

        Assert.Equal(2, _openedUrls.Count);
        Assert.Equal(_openedUrls[0], _openedUrls[1]);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
    }

    // ---------------------------------------------------------------- Gespeicherte Anmeldung

    [Fact]
    public async Task Restore_NothingSaved_IsSignedOut()
    {
        var check = await CreateService().RestoreAsync(default);

        Assert.Equal(new SessionCheck(SessionStatus.SignedOut, null), check);
        Assert.Empty(CreateService().GetGameEnvironment());
    }

    [Fact]
    public async Task Restore_ValidSession_UpdatesName()
    {
        await CreateService().SignInAsync(default);
        _backend.DisplayName = "Jan (neu)";
        var service = CreateService();

        var check = await service.RestoreAsync(default);

        Assert.Equal(new SessionCheck(SessionStatus.SignedIn, "Jan (neu)"), check);
        Assert.Equal("Jan (neu)", _store.Load()!.DisplayName);
        Assert.Equal(FakeBackend.Token, service.GetGameEnvironment()[LaunchEnvironment.SessionToken]);
    }

    [Fact]
    public async Task Restore_SessionRevoked_DeletesIt()
    {
        await CreateService().SignInAsync(default);
        _backend.TokenValid = false;

        var check = await CreateService().RestoreAsync(default);

        Assert.Equal(SessionStatus.SignedOut, check.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Restore_BackendDown_StaysSignedInOffline()
    {
        await CreateService().SignInAsync(default);
        _backend.Down = true;
        var service = CreateService();

        var check = await service.RestoreAsync(default);

        Assert.Equal(new SessionCheck(SessionStatus.Offline, "Jan"), check);
        Assert.NotNull(_store.Load());
        Assert.Equal(FakeBackend.Token, service.GetGameEnvironment()[LaunchEnvironment.SessionToken]);
    }

    [Fact]
    public async Task Restore_TokenFromOtherBackend_IsDropped()
    {
        await CreateService().SignInAsync(default);

        var check = await CreateService("https://staging.test").RestoreAsync(default);

        Assert.Equal(SessionStatus.SignedOut, check.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task SignOut_DeletesSession_AndTellsBackend()
    {
        var service = CreateService();
        await service.SignInAsync(default);

        await service.SignOutAsync();

        Assert.Null(_store.Load());
        Assert.True(_backend.LoggedOut);
        Assert.Empty(service.GetGameEnvironment());
    }

    [Fact]
    public async Task SignOut_BackendDown_StillSignsOutLocally()
    {
        var service = CreateService();
        await service.SignInAsync(default);
        _backend.Down = true;

        await service.SignOutAsync();

        Assert.Null(_store.Load());
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    /// <summary>Nachgebildetes Backend: prüft den PKCE-Verifier wie das echte.</summary>
    private sealed class FakeBackend : HttpMessageHandler
    {
        public const string Code = "einmal-code";
        public const string Token = "aos2_token";

        public string? Challenge { get; set; }
        public string? Verifier { get; private set; }
        public bool VerifierMatched { get; private set; }
        public bool Down { get; set; }
        public bool RejectCode { get; set; }
        public bool TokenValid { get; set; } = true;
        public bool LoggedOut { get; private set; }
        public string DisplayName { get; set; } = "Jan";

        private AccountInfo Account => new() { Id = Guid.Parse("01a19d8a-a74b-75ac-818f-434f56becf9b"), DisplayName = DisplayName };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
                throw new HttpRequestException("Verbindung abgelehnt");

            var path = request.RequestUri!.AbsolutePath;
            var authorized = request.Headers.Authorization?.ToString() == $"Bearer {Token}" && TokenValid;
            switch (path)
            {
                case ApiRoutes.Health:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };

                case ApiRoutes.Session:
                    var body = (await request.Content!.ReadFromJsonAsync<SessionRequest>(cancellationToken))!;
                    Verifier = body.CodeVerifier;
                    var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(body.CodeVerifier)));
                    VerifierMatched = expected == Challenge;
                    if (RejectCode || body.Code != Code || !VerifierMatched)
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                    return Json(new SessionResponse { Token = Token, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), Account = Account });

                case ApiRoutes.Me:
                    return authorized ? Json(Account) : new HttpResponseMessage(HttpStatusCode.Unauthorized);

                case ApiRoutes.Logout:
                    LoggedOut = authorized;
                    return new HttpResponseMessage(authorized ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
