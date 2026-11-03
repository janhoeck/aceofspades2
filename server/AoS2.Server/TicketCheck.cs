using AoS2.Contracts;
using AoS2.JoinTickets;
using AoS2.Shared.Net;

namespace AoS2.Server;

/// <summary>
/// Prüft Join-Tickets beim Beitritt: echte Tickets mit dem öffentlichen Schlüssel des Backends, im Entwicklungsmodus
/// zusätzlich Entwicklungs-Tickets. Jedes Ticket gilt nur einmal (gegen abgefangene und wiederholte Tickets).
/// </summary>
public sealed class TicketCheck
{
    private readonly JoinTicketValidator? _backend;
    private readonly JoinTicketValidator? _development;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public TicketCheck(JoinTicketValidator? backend, JoinTicketValidator? development, TimeProvider time)
    {
        if (backend == null && development == null) throw new ArgumentException("Mindestens ein Prüfer ist nötig.");
        _backend = backend;
        _development = development;
        _time = time;
    }

    public bool AcceptsDevelopmentTickets => _development != null;

    /// <summary>Prüft Signatur, Empfänger und Ablauf. Darf auf einem anderen Thread laufen.</summary>
    public async Task<JoinTicket?> ValidateAsync(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        if (_backend != null && await _backend.ValidateAsync(ticket) is { } real) return real;
        if (_development != null && await _development.ValidateAsync(ticket) is { } dev) return dev;
        return null;
    }

    /// <summary>Meldung für ein ungültiges Ticket.</summary>
    public string InvalidTicketMessage => _development == null
        ? "Das Join-Ticket ist ungültig oder abgelaufen. Bitte über den Launcher anmelden (Entwicklungs-Tickets nimmt nur ein Server mit --dev an)."
        : "Das Join-Ticket ist ungültig oder abgelaufen.";

    /// <summary>Markiert ein gültiges Ticket als benutzt. <c>false</c>, wenn es schon einmal benutzt wurde. Nur vom Haupt-Thread.</summary>
    public bool TryUse(JoinTicket ticket)
    {
        var now = _time.GetUtcNow();
        foreach (var (id, expires) in _used.ToList())
            if (expires + JoinTicketValidator.ClockSkew < now) _used.Remove(id);
        return _used.TryAdd(ticket.TicketId, ticket.ExpiresAt);
    }

    /// <summary>Prüfer so, wie die Optionen es verlangen. Lädt die öffentlichen Schlüssel von der Website.</summary>
    public static async Task<TicketCheck> CreateAsync(ServerOptions options, HttpClient http, TimeProvider time, CancellationToken cancellationToken)
    {
        JoinTicketValidator? backend = null;
        if (options.SiteUrl != null)
        {
            var keys = await http.GetStringAsync(options.SiteUrl + ApiRoutes.JoinTicketKeys, cancellationToken);
            backend = new JoinTicketValidator(keys, options.SiteUrl, options.Address!, time);
        }
        var development = options.DevMode ? JoinTicketValidator.ForDevelopment(DevJoinTicket.Key, DevJoinTicket.Issuer, time) : null;
        return new TicketCheck(backend, development, time);
    }
}
