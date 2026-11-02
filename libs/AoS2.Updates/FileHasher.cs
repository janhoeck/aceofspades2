using System.Security.Cryptography;

namespace AoS2.Updates;

public static class FileHasher
{
    /// <summary>Berechnet die SHA-256-Prüfsumme einer Datei als Hex-Text in Kleinbuchstaben.</summary>
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1 << 16,
        });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
