using System.Net;

namespace AoS2.Updates.Tests;

public class UpdateSourceTests
{
    [Theory]
    [InlineData("https://updates.example.com")]
    [InlineData("http://localhost:8080/aos2/")]
    public void Create_HttpUrl_ReturnsHttpSource(string location)
    {
        Assert.IsType<HttpUpdateSource>(UpdateSource.Create(location, new HttpClient()));
    }

    [Theory]
    [InlineData(@"C:\aos2\release")]
    [InlineData("artifacts/release")]
    public void Create_Path_ReturnsDirectorySource(string location)
    {
        Assert.IsType<DirectoryUpdateSource>(UpdateSource.Create(location, new HttpClient()));
    }

    [Theory]
    [InlineData("https://cdn.example.com/aos2")]
    [InlineData("https://cdn.example.com/aos2/")]
    public async Task Http_CombinesBaseUrlAndPath(string baseUrl)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var source = new HttpUpdateSource(new HttpClient(handler), new Uri(baseUrl));

        await using var _ = await source.OpenReadAsync("game/manifest.json", bypassCache: false, CancellationToken.None);

        Assert.Equal("https://cdn.example.com/aos2/game/manifest.json", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Http_BypassCache_SendsNoCache()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var source = new HttpUpdateSource(new HttpClient(handler), new Uri("https://cdn.example.com"));

        await using var _ = await source.OpenReadAsync("game/manifest.json", bypassCache: true, CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Http_NotFound_ThrowsWithStatusCode()
    {
        var source = new HttpUpdateSource(new HttpClient(new RecordingHandler(HttpStatusCode.NotFound)), new Uri("https://cdn.example.com"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.OpenReadAsync("game/manifest.json", false, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
