using System.Net;
using System.Net.Sockets;
using System.Text;
using AoS2.Contracts;

namespace AoS2.Launcher.Services;

/// <summary>Was der Browser am Ende der Anmeldung an den Launcher weitergereicht hat.</summary>
/// <param name="Code">Einmal-Code, gegen den das Backend ein Sitzungstoken ausgibt.</param>
/// <param name="Error">Stattdessen ein Fehler, z. B. <see cref="LoginErrors.AccessDenied"/>.</param>
public sealed record LoopbackResult(string? Code, string? Error);

/// <summary>
/// Ein winziger Webserver nur für diesen einen Zweck: Er wartet auf <c>http://127.0.0.1:{Port}/callback</c>,
/// bis das Backend den Browser mit dem Einmal-Code dorthin schickt, und zeigt dann eine Abschlussseite.
/// <para>
/// Lauscht nur auf 127.0.0.1 (von außen nicht erreichbar) und auf einem zufälligen freien Port. Bewusst ein
/// einfacher TCP-Listener statt HttpListener: Der bräuchte unter Windows je nach Adresse Administratorrechte.
/// </para>
/// </summary>
public sealed class LoopbackListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackListener()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>
    /// Wartet auf die Weiterleitung mit dem passenden <paramref name="expectedState"/>.
    /// Anfragen mit anderem State (z. B. ein alter Tab) werden beantwortet, aber ignoriert.
    /// </summary>
    public async Task<LoopbackResult> WaitAsync(string expectedState, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var result = new TaskCompletionSource<LoopbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() => result.TrySetCanceled(cancellationToken));

        // Jede Verbindung einzeln bearbeiten: Browser öffnen gern vorab leere Verbindungen,
        // die sonst die eigentliche Anfrage aufhalten würden.
        var connections = new List<Task>();
        try
        {
            while (!result.Task.IsCompleted)
            {
                var accept = _listener.AcceptTcpClientAsync(stop.Token).AsTask();
                if (await Task.WhenAny(accept, result.Task) != accept)
                    break;
                var client = await accept;
                connections.Add(HandleAsync(client, expectedState, result, stop.Token));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }

        var outcome = await result.Task;
        // Die Antwortseite der erfolgreichen Verbindung fertig schicken, offene Leerverbindungen schließen.
        await stop.CancelAsync();
        await Task.WhenAll(connections);
        return outcome;
    }

    private static async Task HandleAsync(TcpClient client, string expectedState, TaskCompletionSource<LoopbackResult> result, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            var target = await ReadRequestTargetAsync(stream, timeout.Token);
            if (target is null)
                return;

            var question = target.IndexOf('?');
            var path = question < 0 ? target : target[..question];
            if (path != LoginParameters.LoopbackPath)
            {
                await RespondAsync(stream, 404, "Nicht gefunden", "Hier gibt es nichts zu sehen.", CancellationToken.None);
                return;
            }

            var query = ParseQuery(question < 0 ? "" : target[(question + 1)..]);
            if (query.GetValueOrDefault(LoginParameters.State) != expectedState)
            {
                await RespondAsync(stream, 400, "Veraltete Anmeldung",
                    "Diese Seite gehört zu einer älteren Anmeldung. Schau bitte im Launcher nach.", CancellationToken.None);
                return;
            }

            var code = query.GetValueOrDefault(LoginParameters.Code);
            var error = query.GetValueOrDefault(LoginParameters.Error);
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error))
                error = LoginErrors.DiscordFailed;

            if (error is null)
                await RespondAsync(stream, 200, "Angemeldet", "Du kannst dieses Fenster schließen und zum Launcher zurückkehren.", CancellationToken.None);
            else
                await RespondAsync(stream, 200, "Anmeldung abgebrochen", "Im Launcher kannst du es noch einmal versuchen.", CancellationToken.None);

            result.TrySetResult(new LoopbackResult(error is null ? code : null, error));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Leere oder abgebrochene Verbindung: einfach schließen.
        }
    }

    /// <summary>Liest die erste Zeile („GET /callback?… HTTP/1.1“) und den Rest des Kopfes. Gibt das Ziel zurück.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                break;
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var parts = (lineEnd < 0 ? head : head[..lineEnd]).Split(' ');
        return parts is ["GET", var target, var version] && version.StartsWith("HTTP/1.", StringComparison.Ordinal) ? target : null;
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            result.TryAdd(key, equals < 0 ? "" : Decode(pair[(equals + 1)..]));
        }
        return result;

        static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string text, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(Page(title, text));
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Gleiche Farben wie Launcher und Backend-Seiten.</summary>
    private static string Page(string title, string text) => $$"""
        <!doctype html>
        <html lang="de">
        <head>
        <meta charset="utf-8">
        <title>{{WebUtility.HtmlEncode(title)}} – Ace of Spades 2</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #14181F; color: #EDE6D3;
                 font: 17px/1.5 "Barlow", "Segoe UI", system-ui, sans-serif; }
          main { max-width: 480px; margin: 24px; padding: 28px 32px; background: #0B0E13; border: 2px solid #2A3240; border-bottom-width: 6px; }
          h1 { margin: 0 0 12px; color: #F2C14E; font-size: 26px; }
          p { margin: 0; color: #C9C2AF; }
        </style>
        </head>
        <body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main></body>
        </html>
        """;

    public void Dispose() => _listener.Dispose();
}
