using System.Security.Cryptography;
using System.Text;
using AoS2.Contracts;
using AoS2.Launcher.Services;
using AoS2.Updates.Tests;

namespace AoS2.Launcher.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static SavedSession Session() => new()
    {
        Token = "aos2_geheim",
        SiteUrl = "https://api.example.org",
        AccountId = Guid.NewGuid(),
        DisplayName = "Jan",
    };

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store = new SessionStore(_temp.Combine("session.dat"), new XorProtector());
        var session = Session();

        store.Save(session);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(session.Token, loaded.Token);
        Assert.Equal(session.SiteUrl, loaded.SiteUrl);
        Assert.Equal(session.AccountId, loaded.AccountId);
        Assert.Equal(session.DisplayName, loaded.DisplayName);
    }

    [Fact]
    public void File_DoesNotContainTokenInPlainText()
    {
        var store = new SessionStore(_temp.Combine("session.dat"), new XorProtector());

        store.Save(Session());

        Assert.DoesNotContain("aos2_geheim", Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)));
    }

    [Fact]
    public void Load_NoFile_IsNull() => Assert.Null(new SessionStore(_temp.Combine("fehlt.dat"), new XorProtector()).Load());

    [Fact]
    public void Load_CannotDecrypt_IsNull()
    {
        var path = _temp.Combine("session.dat");
        new SessionStore(path, new XorProtector()).Save(Session());

        Assert.Null(new SessionStore(path, new FailingProtector()).Load());
    }

    [Fact]
    public void Load_Garbage_IsNull()
    {
        var path = _temp.Combine("session.dat");
        File.WriteAllText(path, "kein json");

        Assert.Null(new SessionStore(path, new XorProtector()).Load());
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = new SessionStore(_temp.Combine("session.dat"), new XorProtector());
        store.Save(Session());

        store.Delete();
        store.Delete();

        Assert.False(File.Exists(store.Path));
        Assert.Null(store.Load());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Dpapi_RoundTrips_AndNeedsEntropy()
    {
        // DPAPI gibt es nur unter Windows; die CI läuft unter Linux.
        if (!OperatingSystem.IsWindows())
            return;

        var store = new SessionStore(_temp.Combine("session.dat"), new DpapiProtector());
        store.Save(Session());

        Assert.Equal("aos2_geheim", store.Load()!.Token);
        // Ohne den Zusatzschlüssel (wie ihn andere Programme versuchen würden) lässt sich nichts entschlüsseln.
        Assert.ThrowsAny<CryptographicException>(() =>
            ProtectedData.Unprotect(File.ReadAllBytes(store.Path), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>Unsicher, aber genug, um die Dateilogik ohne Windows zu testen.</summary>
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => throw new CryptographicException();
        public byte[] Unprotect(byte[] data) => throw new CryptographicException("anderer Benutzer");
    }
}
