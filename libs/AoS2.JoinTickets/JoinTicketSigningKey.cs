using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace AoS2.JoinTickets;

/// <summary>
/// Schlüsselpaar zum Signieren der Join-Tickets (ECDSA P-256, JWT-Algorithmus ES256).
/// Den privaten Teil kennt nur das Backend; den öffentlichen veröffentlicht es als JSON Web Key Set,
/// damit jeder Spielserver Tickets prüfen kann, ohne selbst welche ausstellen zu können.
/// </summary>
public sealed class JoinTicketSigningKey : IDisposable
{
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256;

    private readonly ECDsa _key;
    private readonly string _x;
    private readonly string _y;

    private JoinTicketSigningKey(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new ArgumentException("Der Schlüssel muss die Kurve P-256 nutzen.", nameof(key));

        _key = key;
        _x = Base64UrlEncoder.Encode(parameters.Q.X);
        _y = Base64UrlEncoder.Encode(parameters.Q.Y);
        KeyId = ComputeThumbprint(_x, _y);
        SecurityKey = new ECDsaSecurityKey(key) { KeyId = KeyId };
    }

    /// <summary>JWK-Fingerabdruck (RFC 7638). Steht im Kopf jedes Tickets, damit Server den passenden Schlüssel finden.</summary>
    public string KeyId { get; }

    internal ECDsaSecurityKey SecurityKey { get; }

    public static JoinTicketSigningKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Liest einen privaten Schlüssel im PKCS#8-Format: als PEM (<c>-----BEGIN PRIVATE KEY-----</c>)
    /// oder als Base64 in einer Zeile (bequemer für Umgebungsvariablen), wie <see cref="ExportPrivateKey"/> ihn schreibt.
    /// </summary>
    public static JoinTicketSigningKey Import(string privateKey)
    {
        var key = ECDsa.Create();
        try
        {
            var text = privateKey.Trim();
            if (text.StartsWith("-----BEGIN", StringComparison.Ordinal))
                key.ImportFromPem(text);
            else
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(text), out _);
            return new JoinTicketSigningKey(key);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new FormatException("Der Signaturschlüssel ist kein gültiger privater P-256-Schlüssel (PKCS#8, PEM oder Base64).", ex);
        }
    }

    /// <summary>Privater Schlüssel als PKCS#8 in Base64, eine Zeile.</summary>
    public string ExportPrivateKey() => Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

    /// <summary>Öffentlicher Schlüssel als JSON Web Key Set: <c>{"keys":[{…}]}</c>.</summary>
    public string ExportPublicKeySet() => JsonSerializer.Serialize(new
    {
        keys = new[]
        {
            new Dictionary<string, string>
            {
                ["kty"] = "EC",
                ["use"] = "sig",
                ["alg"] = Algorithm,
                ["kid"] = KeyId,
                ["crv"] = "P-256",
                ["x"] = _x,
                ["y"] = _y,
            },
        },
    });

    public void Dispose() => _key.Dispose();

    private static string ComputeThumbprint(string x, string y)
    {
        // RFC 7638: Pflichtfelder in alphabetischer Reihenfolge, ohne Leerzeichen.
        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""";
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
