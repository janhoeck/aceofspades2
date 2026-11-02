using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AoS2.Contracts;

namespace AoS2.Launcher.Services;

/// <summary>Verschlüsselt die gespeicherte Anmeldung. In Tests durch eine Attrappe ersetzt (DPAPI gibt es nur unter Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    /// <exception cref="CryptographicException">Nicht von diesem Windows-Benutzer verschlüsselt oder beschädigt.</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Windows DPAPI: Den Schlüssel verwaltet Windows, er hängt am Windows-Konto. Kopiert jemand die Datei
/// auf einen anderen PC oder liest sie als anderer Benutzer, kann er sie nicht entschlüsseln.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(SavedSession.Entropy);

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Speichert die Anmeldung als verschlüsseltes JSON in <c>session.dat</c>.</summary>
public sealed class SessionStore(string path, ISecretProtector protector)
{
    public string Path { get; } = path;

    /// <summary>Fehlt die Datei oder lässt sie sich nicht lesen, ist man eben nicht angemeldet.</summary>
    public SavedSession? Load()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            var json = protector.Unprotect(File.ReadAllBytes(Path));
            var session = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.SavedSession);
            return string.IsNullOrEmpty(session?.Token) || string.IsNullOrEmpty(session.SiteUrl) ? null : session;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(SavedSession session)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var data = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(session, LauncherJsonContext.Default.SavedSession));
        var temp = Path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, Path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
