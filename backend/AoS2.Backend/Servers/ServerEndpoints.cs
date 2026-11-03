using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AoS2.Backend.Auth;
using AoS2.Backend.Data;
using AoS2.Contracts;
using AoS2.Shared.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AoS2.Backend.Servers;

public sealed class ServerBrowserOptions
{
    public const string Section = "ServerBrowser";

    /// <summary>So oft schicken Server ihren Heartbeat.</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>Ohne Heartbeat fällt ein Server nach so vielen Sekunden aus der Liste (drei verpasste Heartbeats).</summary>
    public int ExpirySeconds { get; set; } = 90;

    /// <summary>Höchstens so viele Community-Server pro IP-Adresse gleichzeitig in der Liste.</summary>
    public int MaxServersPerIp { get; set; } = 8;

    /// <summary>Höchstens so viele Erreichbarkeitsprüfungen pro IP-Adresse in 10 Minuten (jede neue Anmeldung ist eine).</summary>
    public int MaxProbesPerIp { get; set; } = 20;

    /// <summary>
    /// Schlüssel für offizielle Server (lang und zufällig; leer = es gibt keine). Sie stehen in der Liste oben, sind als
    /// „offiziell“ markiert und zählen nicht zur Grenze pro IP. Die Server bekommen ihn über <c>AOS2_OFFICIAL_KEY</c>.
    /// </summary>
    public string OfficialKey { get; set; } = "";

    /// <summary>Nur zum Testen: Server mit privaten Adressen (127.0.0.1, 192.168.…) zulassen. In Development an.</summary>
    public bool AllowPrivateAddresses { get; set; }
}

/// <summary>
/// Server-Browser (Master-Server). Spielserver melden sich per Heartbeat an; beim ersten Heartbeat (und wenn sich ihr
/// Geheimnis ändert, also nach einem Neustart) prüft das Backend per UDP, ob sie unter ihrer Adresse erreichbar sind
/// (<see cref="IServerProbe"/>). Das Spiel lädt die Liste öffentlich und misst den Ping selbst.
/// <para>
/// Schutz gegen Spam ohne Server-Schlüssel (Entscheidung M7): Erreichbarkeitsprüfung mit Nachweis, höchstens
/// <see cref="ServerBrowserOptions.MaxServersPerIp"/> Server und <see cref="ServerBrowserOptions.MaxProbesPerIp"/> Prüfungen
/// pro IP, Heartbeats pro IP begrenzt, Einträge ohne Heartbeat fallen nach <see cref="ServerBrowserOptions.ExpirySeconds"/> heraus.
/// </para>
/// </summary>
internal static class ServerEndpoints
{
    public const string HeartbeatRateLimit = "server-heartbeat";

    /// <summary>Abgelaufene Einträge bleiben so lange in der Datenbank (ein kurz unterbrochener Server kommt mit neuer Prüfung zurück).</summary>
    public static readonly TimeSpan DeleteAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ProbeWindow = TimeSpan.FromMinutes(10);

    public static void AddServerBrowser(this IServiceCollection services, IConfiguration config, bool isDevelopment)
    {
        services.AddOptions<ServerBrowserOptions>()
            .Configure(o => o.AllowPrivateAddresses = isDevelopment)
            .Bind(config.GetSection(ServerBrowserOptions.Section))
            .Validate(o => o.HeartbeatSeconds is >= 5 and <= 300 && o.ExpirySeconds > o.HeartbeatSeconds,
                "ServerBrowser: HeartbeatSeconds (5 bis 300) muss kleiner als ExpirySeconds sein.")
            .Validate(o => o.OfficialKey.Length is 0 or >= BackendOptions.MinInternalKeyLength,
                $"ServerBrowser:OfficialKey muss leer oder mindestens {BackendOptions.MinInternalKeyLength} Zeichen lang sein.")
            .ValidateOnStart();
        services.AddSingleton<IServerProbe, UdpServerProbe>();
        services.AddSingleton(provider => new ProbeLimiter(provider.GetRequiredService<IOptions<ServerBrowserOptions>>().Value.MaxProbesPerIp));
    }

    public static void MapServerBrowser(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Servers, ListAsync);
        app.MapPost(ApiRoutes.ServerHeartbeat, HeartbeatAsync).RequireRateLimiting(HeartbeatRateLimit);
        app.MapPost(ApiRoutes.ServerRemove, RemoveAsync).RequireRateLimiting(HeartbeatRateLimit);
    }

    // ---------------------------------------------------------------- Liste

    private static async Task<IResult> ListAsync(int? protocol, HttpContext http, BackendDb db, TimeProvider time,
        IOptions<ServerBrowserOptions> options, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(options.Value.ExpirySeconds);
        var query = db.ListedServers.Where(s => s.LastHeartbeatAt >= since);
        if (protocol is { } version) query = query.Where(s => s.ProtocolVersion == version);
        var servers = await query
            .OrderByDescending(s => s.Official).ThenByDescending(s => s.Players).ThenBy(s => s.Address)
            .Select(s => new ServerListEntry
            {
                Address = s.Address,
                Name = s.Name,
                Map = s.Map,
                Players = s.Players,
                MaxPlayers = s.MaxPlayers,
                ProtocolVersion = s.ProtocolVersion,
                GameVersion = s.GameVersion,
                Official = s.Official,
            })
            .ToListAsync(cancellationToken);

        // Kurz zwischenspeichern: Viele Spieler öffnen gleichzeitig den Browser, die Liste ändert sich nur alle 30 s.
        http.Response.Headers.CacheControl = "public, max-age=5";
        return Results.Ok(new ServerListResponse { Servers = servers });
    }

    // ---------------------------------------------------------------- Heartbeat

    private static async Task<IResult> HeartbeatAsync(ServerHeartbeatRequest request, HttpContext http, BackendDb db, TimeProvider time,
        IServerProbe probe, ProbeLimiter probes, IOptions<ServerBrowserOptions> settings, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (Validate(request) is { } invalid) return Problem(400, invalid);
        var address = request.Address.Trim().ToLowerInvariant();
        ServerAddress.TryParse(address, out var parsed);
        var secret = Convert.FromBase64String(request.Secret);
        var secretHash = Convert.ToHexStringLower(SHA256.HashData(secret));

        var official = false;
        if (!string.IsNullOrEmpty(request.OfficialKey))
        {
            official = options.OfficialKey.Length > 0 && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.OfficialKey), Encoding.UTF8.GetBytes(options.OfficialKey));
            if (!official) return Problem(403, "Der Schlüssel für offizielle Server ist falsch.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var activeSince = now - TimeSpan.FromSeconds(options.ExpirySeconds);
        var ip = ClientIp(http);
        var existing = await db.ListedServers.FirstOrDefaultAsync(s => s.Address == address, cancellationToken);

        // Bekannter Server mit demselben Geheimnis, noch in der Liste: nur die Angaben aktualisieren.
        var known = existing is not null && existing.SecretHash == secretHash && existing.LastHeartbeatAt >= activeSince;
        if (!known)
        {
            // Neue Anmeldung (oder Neustart, oder ein anderer Server will die Adresse): erst Grenzen, dann die Prüfung.
            if (!official)
            {
                var fromIp = await db.ListedServers.CountAsync(s => s.RegisteredFromIp == ip && !s.Official && s.Address != address
                    && s.LastHeartbeatAt >= activeSince, cancellationToken);
                if (fromIp >= options.MaxServersPerIp)
                    return Problem(429, $"Von dieser IP-Adresse sind schon {fromIp} Server eingetragen (höchstens {options.MaxServersPerIp}).");
                if (!probes.TryAcquire(ip))
                {
                    http.Response.Headers.RetryAfter = ((int)ProbeWindow.TotalSeconds / 2).ToString();
                    return Problem(429, "Zu viele Anmeldeversuche von dieser IP-Adresse. Bitte später erneut versuchen.");
                }
            }

            var endPoint = await ResolveAsync(parsed, options.AllowPrivateAddresses, cancellationToken);
            if (endPoint.Error is { } resolveError) return Problem(422, resolveError);

            var (outcome, info) = await probe.ProbeAsync(endPoint.Value!, secret, cancellationToken);
            var log = loggers.CreateLogger(typeof(ServerEndpoints));
            switch (outcome)
            {
                case ProbeOutcome.NoAnswer:
                    log.LogInformation("Server {Address} ({Ip}) nicht erreichbar", address, ip);
                    return Problem(422, $"Der Server ist unter {address} von außen nicht erreichbar (keine Antwort über UDP). "
                        + "Ist der UDP-Port im Router weitergeleitet und in der Firewall freigegeben, und stimmt address?");
                case ProbeOutcome.WrongServer:
                    log.LogWarning("Unter {Address} antwortet ein anderer Server (Anmeldung von {Ip})", address, ip);
                    return Problem(422, $"Unter {address} antwortet ein anderer Spielserver. Stimmt address?");
            }
            if (info.ProtocolVersion != request.ProtocolVersion)
                return Problem(422, $"Der Server unter {address} meldet Protokoll {info.ProtocolVersion}, der Heartbeat {request.ProtocolVersion}.");

            if (existing is null)
            {
                existing = new ListedServer
                {
                    Address = address, Name = "", Map = "", GameVersion = "", SecretHash = secretHash, RegisteredFromIp = ip,
                };
                db.ListedServers.Add(existing);
            }
            existing.RegisteredAt = now;
            existing.SecretHash = secretHash;
            existing.RegisteredFromIp = ip;
            log.LogInformation("Server {Address} eingetragen: \"{Name}\" von {Ip}{Official}", address, request.Name, ip, official ? " (offiziell)" : "");

            // Gelegenheit zum Aufräumen: lange abgelaufene Einträge löschen.
            var deleteBefore = now - DeleteAfter;
            await db.ListedServers.Where(s => s.LastHeartbeatAt < deleteBefore).ExecuteDeleteAsync(cancellationToken);
        }

        existing!.Name = request.Name;
        existing.Map = request.Map;
        existing.Players = request.Players;
        existing.MaxPlayers = request.MaxPlayers;
        existing.ProtocolVersion = request.ProtocolVersion;
        existing.GameVersion = request.GameVersion ?? "";
        existing.Official = official;
        existing.LastHeartbeatAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Zwei Heartbeats für dieselbe neue Adresse gleichzeitig: der zweite versucht es beim nächsten Mal.
            return Problem(409, "Gleichzeitige Anmeldung derselben Adresse. Bitte erneut versuchen.");
        }

        return Results.Ok(new ServerHeartbeatResponse
        {
            HeartbeatSeconds = options.HeartbeatSeconds,
            ExpirySeconds = options.ExpirySeconds,
            Official = official,
        });
    }

    private static async Task<IResult> RemoveAsync(ServerRemoveRequest request, BackendDb db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Address) || DecodeSecret(request.Secret) is not { } secret) return Problem(400, "Ungültige Abmeldung.");
        var address = request.Address.Trim().ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(secret));
        await db.ListedServers.Where(s => s.Address == address && s.SecretHash == hash).ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- Hilfen

    /// <summary>Fehlermeldung zum Heartbeat oder <c>null</c>, wenn alles stimmt.</summary>
    internal static string? Validate(ServerHeartbeatRequest r)
    {
        if (r.Address is null || r.Address.Length > ListedServer.MaxAddressLength || !ServerAddress.TryParse(r.Address, out var parsed)
            || !r.Address.Trim().EndsWith(":" + parsed.Port))
            return "address muss eine Adresse mit Port sein, z. B. play.example.org:32887.";
        if (r.Name is null || !ServerListing.IsValidName(r.Name))
            return $"Ungültiger Servername (1 bis {ServerListing.MaxNameLength} Zeichen, ohne Steuerzeichen).";
        if (r.Map is null || !ServerListing.IsValidMap(r.Map)) return "Ungültiger Map-Name.";
        if (r.MaxPlayers is < 1 or > NetLimits.MaxPlayers || r.Players < 0 || r.Players > r.MaxPlayers) return "Ungültige Spielerzahl.";
        if (r.GameVersion is { Length: > 32 }) return "Ungültige Version.";
        if (DecodeSecret(r.Secret) is null) return $"secret muss {ServerListing.SecretBytes} Byte als Base64 sein.";
        return null;
    }

    private static byte[]? DecodeSecret(string? value)
    {
        if (value is null || value.Length > 64) return null;
        var bytes = new byte[ServerListing.SecretBytes + 4];
        return Convert.TryFromBase64String(value, bytes, out var length) && length == ServerListing.SecretBytes ? bytes[..length] : null;
    }

    /// <summary>
    /// Adresse auflösen. Private Adressen (Heimnetz, localhost) sind aus dem Internet nicht erreichbar und würden das Backend
    /// Pakete in sein eigenes Netz schicken lassen; deshalb nur mit <see cref="ServerBrowserOptions.AllowPrivateAddresses"/>.
    /// </summary>
    private static async Task<(IPEndPoint? Value, string? Error)> ResolveAsync(ServerAddress address, bool allowPrivate, CancellationToken cancellationToken)
    {
        IPAddress[] candidates;
        if (IPAddress.TryParse(address.Host, out var literal))
        {
            candidates = [literal];
        }
        else
        {
            try
            {
                candidates = await Dns.GetHostAddressesAsync(address.Host, cancellationToken);
            }
            catch (SocketException)
            {
                return (null, $"Der Name {address.Host} lässt sich nicht auflösen (DNS). Stimmt address?");
            }
        }

        // IPv4 zuerst: Damit verbinden sich fast alle Spieler.
        var usable = candidates.Where(a => allowPrivate || IsPublic(a))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
        if (usable is null)
            return (null, $"{address} ist keine öffentliche Adresse (z. B. Heimnetz 192.168.… oder localhost). "
                + "Trage die öffentliche IP-Adresse oder einen Domainnamen ein.");
        return (new IPEndPoint(usable, address.Port), null);
    }

    /// <summary>Aus dem Internet erreichbar? Nicht localhost, Heimnetz, Carrier-NAT, link-local oder Multicast.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 169 && b[1] == 254
                || b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }

    internal static string ClientIp(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "unbekannt";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}

/// <summary>Höchstens so viele Erreichbarkeitsprüfungen pro IP in <see cref="ServerEndpoints.ProbeWindow"/>.</summary>
internal sealed class ProbeLimiter(int permits) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(ip =>
        RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = ServerEndpoints.ProbeWindow,
            SegmentsPerWindow = 10,
        }));

    public bool TryAcquire(string ip)
    {
        using var lease = _limiter.AttemptAcquire(ip);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
