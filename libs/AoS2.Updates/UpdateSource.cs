using System.Net.Http.Headers;

namespace AoS2.Updates;

/// <summary>Woher der Launcher Manifest und Dateien lädt: Web-Server (R2) oder lokaler Ordner.</summary>
public interface IUpdateSource
{
    /// <summary>Für Anzeige und Fehlermeldungen.</summary>
    string Location { get; }

    /// <param name="relativePath">Pfad im Update-Speicher, siehe <see cref="UpdateLayout"/>.</param>
    /// <param name="bypassCache">Für das Manifest: immer die neueste Fassung holen.</param>
    Task<Stream> OpenReadAsync(string relativePath, bool bypassCache, CancellationToken cancellationToken);
}

public static class UpdateSource
{
    /// <summary>
    /// <c>https://…</c> oder <c>http://…</c> wird zur Web-Quelle, alles andere gilt als Ordnerpfad.
    /// Ein Ordner ist praktisch zum lokalen Testen, z. B. <c>artifacts/release</c>.
    /// </summary>
    public static IUpdateSource Create(string location, HttpClient httpClient)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return new HttpUpdateSource(httpClient, uri);

        return new DirectoryUpdateSource(location);
    }
}

public sealed class HttpUpdateSource : IUpdateSource
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public HttpUpdateSource(HttpClient httpClient, Uri baseUri)
    {
        _httpClient = httpClient;
        // Ohne „/“ am Ende würde new Uri(base, "game/…") den letzten Teil der Basis ersetzen.
        _baseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
    }

    public string Location => _baseUri.AbsoluteUri;

    public async Task<Stream> OpenReadAsync(string relativePath, bool bypassCache, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, relativePath));
        if (bypassCache)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException(
                $"{relativePath}: Server antwortet mit {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        // Der Stream gehört ab hier dem Aufrufer. Schließt er ihn, wird die Verbindung wieder frei.
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }
}

public sealed class DirectoryUpdateSource(string rootDirectory) : IUpdateSource
{
    public string Location => Path.GetFullPath(rootDirectory);

    public Task<Stream> OpenReadAsync(string relativePath, bool bypassCache, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootDirectory, relativePath);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Task.FromResult(stream);
    }
}
