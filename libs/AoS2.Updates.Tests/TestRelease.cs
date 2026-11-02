using System.Security.Cryptography;
using System.Text;

namespace AoS2.Updates.Tests;

/// <summary>Temporärer Ordner, der nach dem Test gelöscht wird.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("aos2-tests-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* Aufräumen ist nicht testrelevant. */ }
    }
}

/// <summary>Baut einen Update-Speicher in einem Ordner nach, so wie ihn der ManifestBuilder schreibt.</summary>
public static class TestRelease
{
    public static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static GameManifest CreateManifest(string version, IReadOnlyDictionary<string, string> files, string executable = "AoS2.exe") => new()
    {
        GameVersion = version,
        Executable = executable,
        Files = files
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new ManifestFile(f.Key, Encoding.UTF8.GetByteCount(f.Value), Sha256(f.Value)))
            .ToList(),
    };

    /// <summary>Schreibt Objekte und Manifest in <paramref name="root"/> und gibt das Manifest zurück.</summary>
    public static async Task<GameManifest> PublishAsync(string root, string version, IReadOnlyDictionary<string, string> files)
    {
        var manifest = CreateManifest(version, files);
        foreach (var content in files.Values)
        {
            var objectPath = System.IO.Path.Combine(root, UpdateLayout.ObjectPath(Sha256(content)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(objectPath)!);
            await File.WriteAllTextAsync(objectPath, content);
        }

        var manifestPath = System.IO.Path.Combine(root, UpdateLayout.ManifestPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifestPath)!);
        await using var stream = File.Create(manifestPath);
        await ManifestSerializer.WriteAsync(manifest, stream);
        return manifest;
    }
}

/// <summary>Zählt Zugriffe und kann die ersten Zugriffe absichtlich scheitern lassen.</summary>
public sealed class CountingSource(IUpdateSource inner) : IUpdateSource
{
    private int _failuresLeft;

    public List<string> Requests { get; } = [];
    public string Location => inner.Location;

    public void FailNext(int count) => _failuresLeft = count;

    public Task<Stream> OpenReadAsync(string relativePath, bool bypassCache, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(relativePath);
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new HttpRequestException("Simulierter Netzwerkfehler");
            }
        }
        return inner.OpenReadAsync(relativePath, bypassCache, cancellationToken);
    }
}
