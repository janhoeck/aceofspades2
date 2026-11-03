using System.Globalization;

namespace AoS2.Shared.Net
{
    /// <summary>Adresse eines Spielservers, wie Spieler sie eingeben: <c>host</c>, <c>host:port</c> oder <c>[IPv6]:port</c>.</summary>
    public struct ServerAddress
    {
        public string Host;
        public int Port;

        /// <summary>So steht der Server im Join-Ticket (immer mit Port).</summary>
        public override string ToString() => (Host.Contains(":") ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

        public static bool TryParse(string text, out ServerAddress address)
        {
            address = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            string host;
            var port = ProtocolInfo.DefaultPort;

            if (s.StartsWith("["))
            {
                var end = s.IndexOf(']');
                if (end < 2) return false;
                host = s.Substring(1, end - 1);
                var rest = s.Substring(end + 1);
                if (rest.Length > 0 && (!rest.StartsWith(":") || !TryPort(rest.Substring(1), out port))) return false;
            }
            else
            {
                var colon = s.LastIndexOf(':');
                if (colon >= 0 && s.IndexOf(':') != colon) return false; // IPv6 nur in eckigen Klammern
                host = colon >= 0 ? s.Substring(0, colon) : s;
                if (colon >= 0 && !TryPort(s.Substring(colon + 1), out port)) return false;
            }

            if (host.Length == 0 || host.IndexOf(' ') >= 0 || host.IndexOf('/') >= 0) return false;
            address = new ServerAddress { Host = host, Port = port };
            return true;
        }

        private static bool TryPort(string text, out int port) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
    }
}
