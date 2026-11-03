using System.Collections.Generic;

namespace AoS2.Contracts
{
    /// <summary>
    /// Heartbeat eines Spielservers (<c>POST /api/servers/heartbeat</c>, etwa alle <see cref="ServerHeartbeatResponse.HeartbeatSeconds"/>).
    /// Beim ersten Heartbeat prüft das Backend per UDP, ob der Server unter <see cref="Address"/> erreichbar ist.
    /// </summary>
    public sealed class ServerHeartbeatRequest
    {
        /// <summary>Adresse, wie Spieler sie eingeben und wie sie im Join-Ticket steht, z. B. <c>play.example.org:32887</c>.</summary>
        public string Address { get; set; }

        public string Name { get; set; }

        public string Map { get; set; }

        public int Players { get; set; }

        public int MaxPlayers { get; set; }

        public int ProtocolVersion { get; set; }

        /// <summary>Version des Servers (z. B. <c>0.7.0</c>), nur zur Anzeige.</summary>
        public string GameVersion { get; set; }

        /// <summary>
        /// Zufälliges Geheimnis des Servers (Base64, 32 Byte), neu bei jedem Start. Damit beweist er bei der
        /// Erreichbarkeitsprüfung, dass er hinter der Adresse steckt, und nur er kann den Eintrag später ändern.
        /// </summary>
        public string Secret { get; set; }

        /// <summary>Nur offizielle Server: Schlüssel aus der Backend-Config. Sonst <c>null</c>.</summary>
        public string OfficialKey { get; set; }
    }

    public sealed class ServerHeartbeatResponse
    {
        /// <summary>Nach so vielen Sekunden den nächsten Heartbeat schicken.</summary>
        public int HeartbeatSeconds { get; set; }

        /// <summary>Ohne Heartbeat fällt der Server nach so vielen Sekunden aus der Liste.</summary>
        public int ExpirySeconds { get; set; }

        public bool Official { get; set; }
    }

    /// <summary>Server meldet sich ab (<c>POST /api/servers/remove</c>), z. B. beim Beenden.</summary>
    public sealed class ServerRemoveRequest
    {
        public string Address { get; set; }

        public string Secret { get; set; }
    }

    /// <summary>Antwort auf <c>GET /api/servers</c>.</summary>
    public sealed class ServerListResponse
    {
        public List<ServerListEntry> Servers { get; set; } = new List<ServerListEntry>();
    }

    /// <summary>Ein Eintrag im Server-Browser.</summary>
    public sealed class ServerListEntry
    {
        /// <summary>Adresse zum Verbinden; für genau sie holt der Client das Join-Ticket.</summary>
        public string Address { get; set; }

        public string Name { get; set; }

        public string Map { get; set; }

        /// <summary>Stand des letzten Heartbeats. Den aktuellen Stand fragt der Client beim Ping direkt beim Server ab.</summary>
        public int Players { get; set; }

        public int MaxPlayers { get; set; }

        public int ProtocolVersion { get; set; }

        public string GameVersion { get; set; }

        /// <summary>Offizieller Server des Projekts (Coolify).</summary>
        public bool Official { get; set; }
    }
}
