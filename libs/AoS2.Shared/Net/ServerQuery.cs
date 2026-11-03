using System;
using System.Security.Cryptography;

namespace AoS2.Shared.Net
{
    // Abfrage ohne Verbindung (LiteNetLib „unconnected message“): Wer eine ServerQuery an den UDP-Port eines Spielservers
    // schickt, bekommt eine ServerInfo zurück. Das nutzen der Server-Browser im Spiel (Ping und aktuelle Spielerzahl) und
    // das Backend (Erreichbarkeitsprüfung beim ersten Heartbeat).
    //
    // Der Anfang beider Nachrichten (Kennung, Nonce, Protokollversion, Nachweis) bleibt über alle Protokollversionen gleich.
    // So erkennt ein Client auch einen Server mit anderer Version, und das Backend kann jeden Server prüfen.

    /// <summary>Grenzen für Server-Browser und Heartbeat (Server, Backend und Client prüfen dieselben).</summary>
    public static class ServerListing
    {
        /// <summary>Längster Servername in Zeichen.</summary>
        public const int MaxNameLength = 40;

        /// <summary>Längster Map-Name in Zeichen (Dateiname ohne Endung).</summary>
        public const int MaxMapLength = 64;

        /// <summary>Länge des Geheimnisses, mit dem ein Server seine Adresse nachweist (<see cref="ServerProof"/>).</summary>
        public const int SecretBytes = 32;

        /// <summary>
        /// Servername erlaubt? 1 bis <see cref="MaxNameLength"/> Zeichen, keine Steuerzeichen, nicht nur Leerzeichen,
        /// keine Leerzeichen am Anfang oder Ende.
        /// </summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name.Trim() != name) return false;
            foreach (var c in name)
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) return false;
            return true;
        }

        /// <summary>Map-Name erlaubt? Buchstaben, Ziffern, <c>-</c> und <c>_</c>.</summary>
        public static bool IsValidMap(string map)
        {
            if (string.IsNullOrEmpty(map) || map.Length > MaxMapLength) return false;
            foreach (var c in map)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
            return true;
        }
    }

    /// <summary>Anfrage an einen Spielserver (Client oder Backend → Server), immer genau <see cref="Size"/> Byte.</summary>
    public struct ServerQuery
    {
        /// <summary>Kennung „AOSQ“.</summary>
        public const uint Magic = 0x51534F41;

        /// <summary>
        /// Feste Größe. Die Antwort ist höchstens dreimal so groß; so taugt der Server kaum als Verstärker für Angriffe
        /// mit gefälschter Absenderadresse.
        /// </summary>
        public const int Size = 80;

        public const int ChallengeBytes = 32;

        /// <summary>Beliebige Zahl, kommt in der Antwort zurück. Damit ordnet der Client Antworten seinen Anfragen zu.</summary>
        public uint Nonce;

        /// <summary>
        /// Nur das Backend: Zufallswert, den der Server mit seinem Geheimnis unterschreibt (<see cref="ServerProof"/>).
        /// <c>null</c> = kein Nachweis gewünscht.
        /// </summary>
        public byte[] Challenge;

        public byte[] ToBytes()
        {
            if (Challenge != null && Challenge.Length != ChallengeBytes) throw new ArgumentException($"Challenge muss {ChallengeBytes} Byte lang sein.");
            var w = new NetWriter(Size);
            w.WriteUInt(Magic);
            w.WriteUInt(Nonce);
            w.WriteBool(Challenge != null);
            w.WriteBytes(Challenge ?? new byte[ChallengeBytes], 0, ChallengeBytes);
            while (w.Length < Size) w.WriteByte(0);
            return w.ToArray();
        }

        public static ServerQuery Read(NetReader r)
        {
            if (r.Remaining != Size) throw new ProtocolException("Server-Abfrage hat die falsche Größe.");
            if (r.ReadUInt() != Magic) throw new ProtocolException("Keine Server-Abfrage.");
            var query = new ServerQuery { Nonce = r.ReadUInt() };
            var hasChallenge = r.ReadBool();
            var challenge = r.ReadBytes(ChallengeBytes);
            if (hasChallenge) query.Challenge = challenge;
            return query;
        }

        public static ServerQuery Read(byte[] data, int offset, int count) => Read(new NetReader(data, offset, count));
    }

    /// <summary>Antwort auf eine <see cref="ServerQuery"/> (Server → Client oder Backend).</summary>
    public struct ServerInfo
    {
        /// <summary>Kennung „AOSI“.</summary>
        public const uint Magic = 0x49534F41;

        public uint Nonce;

        public int ProtocolVersion;

        /// <summary>HMAC über die Challenge der Anfrage (<see cref="ServerProof"/>), sonst <c>null</c>.</summary>
        public byte[] Proof;

        // Ab hier nur, wenn die Protokollversion passt (bei einer anderen Version kann der Rest anders aussehen).
        public string Name;
        public string Map;
        public int Players;
        public int MaxPlayers;

        public bool IsCompatible => ProtocolInfo.IsCompatible(ProtocolVersion);

        public byte[] ToBytes()
        {
            if (Proof != null && Proof.Length != ServerProof.Bytes) throw new ArgumentException($"Proof muss {ServerProof.Bytes} Byte lang sein.");
            var w = new NetWriter(160);
            w.WriteUInt(Magic);
            w.WriteUInt(Nonce);
            w.WriteInt(ProtocolVersion);
            w.WriteBool(Proof != null);
            if (Proof != null) w.WriteBytes(Proof, 0, Proof.Length);
            w.WriteString(Name ?? "");
            w.WriteString(Map ?? "");
            w.WriteByte((byte)Math.Max(0, Math.Min(255, Players)));
            w.WriteByte((byte)Math.Max(0, Math.Min(255, MaxPlayers)));
            return w.ToArray();
        }

        public static ServerInfo Read(NetReader r)
        {
            if (r.ReadUInt() != Magic) throw new ProtocolException("Keine Server-Antwort.");
            var info = new ServerInfo { Nonce = r.ReadUInt(), ProtocolVersion = r.ReadInt() };
            if (r.ReadBool()) info.Proof = r.ReadBytes(ServerProof.Bytes);
            if (!info.IsCompatible) return info;

            info.Name = r.ReadString(ServerListing.MaxNameLength * 4);
            info.Map = r.ReadString(ServerListing.MaxMapLength);
            info.Players = r.ReadByte();
            info.MaxPlayers = r.ReadByte();
            r.ExpectEnd();
            if (info.Players > info.MaxPlayers) throw new ProtocolException("Mehr Spieler als Plätze.");
            return info;
        }

        public static ServerInfo Read(byte[] data, int offset, int count) => Read(new NetReader(data, offset, count));
    }

    /// <summary>
    /// Nachweis, dass ein Server hinter einer Adresse steckt: Er schickt beim Heartbeat (über HTTPS) ein zufälliges Geheimnis
    /// mit. Das Backend fragt dann per UDP an der angegebenen Adresse mit einer Challenge nach, und nur der Server mit diesem
    /// Geheimnis kann <c>HMAC-SHA256(Geheimnis, Challenge)</c> richtig zurückschicken.
    /// </summary>
    public static class ServerProof
    {
        public const int Bytes = 32;

        public static byte[] Compute(byte[] secret, byte[] challenge)
        {
            using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(challenge);
        }

        /// <summary>Vergleich in fester Zeit (verrät nicht über die Laufzeit, wie viele Bytes stimmen).</summary>
        public static bool Matches(byte[] secret, byte[] challenge, byte[] proof)
        {
            if (proof == null || proof.Length != Bytes) return false;
            var expected = Compute(secret, challenge);
            var diff = 0;
            for (var i = 0; i < Bytes; i++) diff |= expected[i] ^ proof[i];
            return diff == 0;
        }
    }
}
