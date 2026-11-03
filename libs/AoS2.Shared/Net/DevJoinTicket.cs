using System;
using System.Security.Cryptography;
using System.Text;
using AoS2.Shared.Json;

namespace AoS2.Shared.Net
{
    /// <summary>
    /// <b>Nur zum Entwickeln:</b> Join-Tickets, die der Client selbst ausstellt, damit man ohne Backend und Discord-Login
    /// lokal testen kann. Sie sind mit einem Schlüssel signiert, der hier im Quellcode steht – jeder kann sie also fälschen.
    /// Deshalb akzeptiert sie nur ein Spielserver, der ausdrücklich im Entwicklungsmodus läuft (<c>--dev</c>);
    /// ein normaler Server lehnt sie ab, weil er nur Tickets mit der Signatur des Backends annimmt.
    /// <para>
    /// Aufbau wie ein echtes Ticket (JWT), aber mit HMAC-SHA256 (HS256) statt ECDSA: Das läuft auch unter Unitys Mono.
    /// Geprüft wird es auf dem Server mit <c>AoS2.JoinTickets.JoinTicketValidator.ForDevelopment</c>.
    /// </para>
    /// </summary>
    public static class DevJoinTicket
    {
        /// <summary>Aussteller (<c>iss</c>) und Empfänger (<c>aud</c>) aller Entwicklungs-Tickets.</summary>
        public const string Issuer = "aos2-dev";

        /// <summary>Öffentlich bekannter Testschlüssel. Niemals für etwas anderes verwenden.</summary>
        public static byte[] Key => Encoding.ASCII.GetBytes("aos2-dev-only-join-ticket-key-not-secret-0001");

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

        /// <param name="accountId">Beliebige ID; ohne Backend gibt es keine echten Konten.</param>
        public static string Create(Guid accountId, string displayName, DateTimeOffset now)
        {
            var issued = now.ToUnixTimeSeconds();
            var header = JsonValue.NewObject().Set("alg", "HS256").Set("typ", "JWT").ToJson(false);
            var payload = JsonValue.NewObject()
                .Set("iss", Issuer)
                .Set("aud", Issuer)
                .Set("sub", accountId.ToString())
                .Set("name", displayName)
                .Set("jti", Guid.NewGuid().ToString("N"))
                .Set("iat", issued)
                .Set("nbf", issued)
                .Set("exp", issued + (long)Lifetime.TotalSeconds)
                .ToJson(false);

            var unsigned = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
            using (var hmac = new HMACSHA256(Key))
                return unsigned + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned)));
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Prüfsumme einer Map-Datei. Server und Client vergleichen sie beim Beitritt.</summary>
    public static class MapChecksum
    {
        public static byte[] Compute(byte[] mapFile)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(mapFile);
        }

        public static bool AreEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static string ToHex(byte[] checksum)
        {
            var sb = new StringBuilder(checksum.Length * 2);
            foreach (var b in checksum) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
