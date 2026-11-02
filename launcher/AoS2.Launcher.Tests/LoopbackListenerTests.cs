using System.Net;
using System.Net.Sockets;
using System.Text;
using AoS2.Contracts;
using AoS2.Launcher.Services;

namespace AoS2.Launcher.Tests;

public sealed class LoopbackListenerTests : IDisposable
{
    private const string State = "launcher-state";

    private readonly LoopbackListener _listener = new();
    private readonly HttpClient _browser = new();

    public void Dispose()
    {
        _listener.Dispose();
        _browser.Dispose();
    }

    private string Url(string query) => $"http://127.0.0.1:{_listener.Port}{LoginParameters.LoopbackPath}?{query}";

    [Fact]
    public async Task Callback_ReturnsCode_AndShowsPage()
    {
        var wait = _listener.WaitAsync(State, default);

        using var response = await _browser.GetAsync(Url($"code=abc-123&state={State}"));
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new LoopbackResult("abc-123", null), result);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Angemeldet", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callback_WithError_ReturnsError()
    {
        var wait = _listener.WaitAsync(State, default);

        using var response = await _browser.GetAsync(Url($"error={LoginErrors.AccessDenied}&state={State}"));

        Assert.Equal(new LoopbackResult(null, LoginErrors.AccessDenied), await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task OtherState_OtherPath_AndIdleConnections_AreIgnored()
    {
        var wait = _listener.WaitAsync(State, default);

        // Browser öffnen gern vorab Verbindungen, über die nie etwas kommt.
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, _listener.Port);

        using var favicon = await _browser.GetAsync($"http://127.0.0.1:{_listener.Port}/favicon.ico");
        using var oldTab = await _browser.GetAsync(Url("code=alt&state=anderer"));
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oldTab.StatusCode);
        Assert.False(wait.IsCompleted);

        using var response = await _browser.GetAsync(Url($"code=neu&state={State}"));
        Assert.Equal("neu", (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Code);
    }

    [Fact]
    public async Task Garbage_IsIgnored()
    {
        var wait = _listener.WaitAsync(State, default);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, _listener.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("Hallo\r\n\r\n"));
        }

        using var response = await _browser.GetAsync(Url($"code=ok&state={State}"));
        Assert.Equal("ok", (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Code);
    }

    [Theory]
    [InlineData("code=abc&state=" + State, "https://aos2.test/login/done")]
    [InlineData("error=access_denied&state=" + State, "https://aos2.test/login/error?reason=access_denied")]
    public async Task WithWebsite_RedirectsToItsPages(string query, string expected)
    {
        using var listener = new LoopbackListener("https://aos2.test");
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var wait = listener.WaitAsync(State, default);

        using var response = await browser.GetAsync($"http://127.0.0.1:{listener.Port}{LoginParameters.LoopbackPath}?{query}");
        await wait.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Cancel_StopsWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        var wait = _listener.WaitAsync(State, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Listens_OnlyOnLoopback() => Assert.InRange(_listener.Port, 1024, 65535);

    [Theory]
    [InlineData("code=a%2Bb&state=x", "a+b")]
    [InlineData("state=x&code=a+b", "a b")]
    [InlineData("code=&state=x", "")]
    public void ParseQuery_Decodes(string query, string code) => Assert.Equal(code, LoopbackListener.ParseQuery(query)["code"]);
}
