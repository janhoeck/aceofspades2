using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace AoS2.Backend.Auth;

/// <summary>Zufallswerte und ihre Prüfsummen. In der Datenbank stehen nur Prüfsummen, nie die Werte selbst.</summary>
internal static class SecretTokens
{
    /// <summary>Präfix der Sitzungstoken. Hilft Secret-Scannern (z. B. GitHub), versehentlich veröffentlichte Token zu erkennen.</summary>
    public const string SessionPrefix = "aos2_";

    /// <summary>256 Bit Zufall, Base64url.</summary>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string CreateSessionToken() => SessionPrefix + Create();

    /// <summary>SHA-256 als Hex. Reicht hier (anders als bei Passwörtern), weil die Werte zufällig und lang sind.</summary>
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>PKCE (RFC 7636, Methode S256): passt der Verifier zur Challenge vom Anfang der Anmeldung?</summary>
    public static bool MatchesChallenge(string verifier, string challenge)
    {
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>Nur die Zeichen von Base64url, in einer sinnvollen Länge.</summary>
    public static bool IsUrlSafe(string? value, int minLength, int maxLength) =>
        value is not null && value.Length >= minLength && value.Length <= maxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
