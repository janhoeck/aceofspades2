using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AoS2.Contracts;
using AoS2.Shared;
using Microsoft.Extensions.Logging;

namespace AoS2.Server;

/// <summary>
/// Trägt den Server im Server-Browser ein: etwa alle 30 Sekunden ein Heartbeat an das Backend (Name, Map, Spieler,
/// Protokollversion, Adresse). Beim ersten Heartbeat prüft das Backend per UDP, ob der Server unter seiner Adresse von
/// außen erreichbar ist (<see cref="Shared.Net.ServerProof"/>); erst dann erscheint er in der Liste. Ohne Heartbeat fällt
/// er nach kurzer Zeit wieder heraus. Beim Beenden meldet er sich ab.
/// <para>Läuft neben der Hauptschleife (eigener Task); liest vom <see cref="GameServer"/> nur, was dafür threadsicher ist.</para>
/// </summary>
public sealed class Heartbeat
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Umgebungsvariable mit dem Schlüssel für offizielle Server (aus der Backend-Config). Nicht in der Config-Datei, damit er
    /// nicht versehentlich mit ihr geteilt wird.
    /// </summary>
    public const string OfficialKeyVariable = "AOS2_OFFICIAL_KEY";

    /// <summary>Nach einer Ablehnung (z. B. nicht erreichbar) erst nach dieser Pause erneut versuchen.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string _site;
    private readonly GameServer _server;
    private readonly string _address;
    private readonly string? _officialKey;
    private readonly ILogger _log;

    public Heartbeat(HttpClient http, string siteUrl, GameServer server, string address, string? officialKey, ILogger log)
    {
        _http = http;
        _site = siteUrl.TrimEnd('/');
        _server = server;
        _address = address;
        _officialKey = string.IsNullOrWhiteSpace(officialKey) ? null : officialKey.Trim();
        _log = log;
    }

    /// <summary>Steht der Server gerade in der Liste (letzter Heartbeat angenommen)?</summary>
    public bool Listed { get; private set; }

    /// <summary>Warum der letzte Heartbeat abgelehnt wurde, sonst <c>null</c>.</summary>
    public string? Problem { get; private set; }

    public ServerHeartbeatRequest CreateRequest() => new()
    {
        Address = _address,
        Name = _server.Name,
        Map = _server.Match.Content.MapName,
        Players = _server.PlayerCount,
        MaxPlayers = _server.Match.Settings.MaxPlayers,
        ProtocolVersion = ProtocolInfo.Version,
        GameVersion = Version,
        Secret = Convert.ToBase64String(_server.Secret),
        OfficialKey = _officialKey,
    };

    /// <summary>Schickt Heartbeats, bis <paramref name="cancellationToken"/> ausgelöst wird.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(await SendAsync(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server wird beendet (auch mitten in einem Heartbeat).
        }
    }

    /// <summary>Ein Heartbeat. Gibt zurück, wann der nächste fällig ist.</summary>
    public async Task<TimeSpan> SendAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(_site + ApiRoutes.ServerHeartbeat, CreateRequest(), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ServerHeartbeatResponse>(cancellationToken);
                if (!Listed)
                    _log.LogInformation("Im Server-Browser eingetragen ({Site}): \"{Name}\" unter {Address}{Official}",
                        _site, _server.Name, _address, body?.Official == true ? " als offizieller Server" : "");
                Listed = true;
                Problem = null;
                var seconds = body?.HeartbeatSeconds ?? 0;
                return seconds > 0 ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300)) : DefaultInterval;
            }

            var retry = response.Headers.RetryAfter?.Delta;
            Fail($"{await ReadProblemAsync(response, cancellationToken)} (HTTP {(int)response.StatusCode})");
            return retry > RetryAfterFailure ? retry.Value : RetryAfterFailure;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Fail($"Die Website {_site} ist nicht erreichbar ({e.Message})");
            return RetryAfterFailure;
        }
    }

    /// <summary>Beim Beenden: sofort aus der Liste nehmen, statt auf den Ablauf zu warten.</summary>
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (!Listed) return;
        try
        {
            using var response = await _http.PostAsJsonAsync(_site + ApiRoutes.ServerRemove,
                new ServerRemoveRequest { Address = _address, Secret = Convert.ToBase64String(_server.Secret) }, cancellationToken);
            Listed = !response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Dann fällt der Eintrag eben nach dem Ablauf heraus.
        }
    }

    private void Fail(string problem)
    {
        if (Listed || problem != Problem)
            _log.LogWarning("Server-Browser: nicht eingetragen. {Problem}. Neuer Versuch in {Seconds} s.", problem, (int)RetryAfterFailure.TotalSeconds);
        Listed = false;
        Problem = problem;
    }

    /// <summary>Titel aus der Fehlerantwort des Backends (Problem Details), sonst der Statustext.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString()!.TrimEnd('.');
        }
        catch (JsonException)
        {
        }
        return response.ReasonPhrase ?? "Abgelehnt";
    }

    /// <summary>Version dieses Servers (beim Release gesetzt, z. B. <c>0.7.0</c>).</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = typeof(Heartbeat).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+'); // ohne Commit-Kennung
        return plus >= 0 ? version[..plus] : version;
    }
}
