using System.Buffers;
using System.Security.Cryptography;

namespace AoS2.Updates;

/// <summary>
/// Installiert und aktualisiert das Spiel in einem Ordner. Ablauf:
/// <list type="number">
/// <item><see cref="FetchManifestAsync"/>: neues Manifest laden.</item>
/// <item><see cref="CheckAsync"/>: lokale Dateien vergleichen, ergibt einen <see cref="UpdatePlan"/>.</item>
/// <item><see cref="ApplyAsync"/>: nur fehlende/geänderte Dateien laden, Prüfsummen kontrollieren,
/// veraltete Dateien löschen, Installationsstand speichern.</item>
/// </list>
/// </summary>
public sealed class GameInstaller(IUpdateSource source, string installDirectory)
{
    public string InstallDirectory { get; } = Path.GetFullPath(installDirectory);

    public int MaxParallelDownloads { get; init; } = 4;
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private string StagingDirectory => Path.Combine(InstallDirectory, InstallState.FolderName, "download");

    public async Task<GameManifest> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = await source.OpenReadAsync(UpdateLayout.ManifestPath, bypassCache: true, cancellationToken);
        return await ManifestSerializer.ReadAsync(stream, cancellationToken);
    }

    public Task<InstallState?> LoadStateAsync(CancellationToken cancellationToken = default) =>
        InstallState.LoadAsync(InstallDirectory, cancellationToken);

    /// <summary>Voller Pfad einer Datei im Spielordner. Wirft, falls der Pfad hinausführen würde.</summary>
    public string GetFullPath(string relativePath)
    {
        if (!ManifestValidator.IsValidPath(relativePath))
            throw new InvalidManifestException($"Ungültiger Dateipfad „{relativePath}“.");

        var full = Path.GetFullPath(Path.Combine(InstallDirectory, relativePath));
        if (!full.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidManifestException($"Der Pfad „{relativePath}“ liegt außerhalb des Spielordners.");
        return full;
    }

    /// <summary>
    /// Vergleicht das Manifest mit dem Spielordner.
    /// </summary>
    /// <param name="verifyAll">
    /// <c>false</c>: Dateien, deren Größe und Änderungszeit zum gespeicherten Stand passen, gelten als
    /// in Ordnung (schnell). <c>true</c>: von jeder vorhandenen Datei die Prüfsumme neu berechnen.
    /// </param>
    /// <param name="progress">Fortschritt in Bytes, die geprüft werden müssen.</param>
    public async Task<UpdatePlan> CheckAsync(
        GameManifest manifest,
        bool verifyAll,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        var known = new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in state?.Files ?? [])
            known.TryAdd(file.Path, file);

        // Erst ohne Hashen sortieren: fehlt, falsche Größe, laut Stand unverändert oder muss gehasht werden.
        var downloads = new List<ManifestFile>();
        var toHash = new List<(ManifestFile File, string FullPath)>();
        foreach (var file in manifest.Files)
        {
            var fullPath = GetFullPath(file.Path);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length != file.Size)
            {
                downloads.Add(file);
            }
            else if (!verifyAll
                && known.TryGetValue(file.Path, out var installed)
                && installed.Sha256 == file.Sha256
                && installed.Size == info.Length
                && installed.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                // Unverändert seit der letzten Installation.
            }
            else
            {
                toHash.Add((file, fullPath));
            }
        }

        var reporter = new ProgressReporter(progress, toHash.Sum(x => x.File.Size));
        foreach (var (file, fullPath) in toHash)
        {
            var sha256 = await FileHasher.ComputeSha256Async(fullPath, cancellationToken);
            if (sha256 != file.Sha256)
                downloads.Add(file);
            reporter.Add(file.Size);
        }
        reporter.Complete();

        // Gelöscht wird nur, was der Launcher selbst installiert hat. Fremde Dateien bleiben.
        var inManifest = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var deletions = known.Keys
            .Where(path => !inManifest.Contains(path) && ManifestValidator.IsValidPath(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new UpdatePlan(manifest, downloads, deletions);
    }

    /// <summary>Führt den Plan aus und speichert danach den neuen Installationsstand.</summary>
    /// <param name="progress">Fortschritt in geladenen Bytes.</param>
    public async Task ApplyAsync(
        UpdatePlan plan,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Reste eines abgebrochenen Downloads wegräumen.
        if (Directory.Exists(StagingDirectory))
            Directory.Delete(StagingDirectory, recursive: true);

        if (plan.Downloads.Count > 0)
        {
            Directory.CreateDirectory(StagingDirectory);
            var reporter = new ProgressReporter(progress, plan.DownloadSize);
            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(plan.Downloads, options, (file, ct) => DownloadWithRetryAsync(file, reporter, ct));
            reporter.Complete();
            Directory.Delete(StagingDirectory, recursive: true);
        }

        foreach (var path in plan.Deletions)
        {
            var fullPath = GetFullPath(path);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            DeleteEmptyParents(fullPath);
        }

        var installed = plan.Manifest.Files
            .Select(f => new InstalledFile(f.Path, f.Size, f.Sha256, File.GetLastWriteTimeUtc(GetFullPath(f.Path))))
            .ToList();
        var state = new InstallState
        {
            GameVersion = plan.Manifest.GameVersion,
            Executable = plan.Manifest.Executable,
            Files = installed,
        };
        await state.SaveAsync(InstallDirectory, cancellationToken);
    }

    private async ValueTask DownloadWithRetryAsync(ManifestFile file, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            long received = 0;
            try
            {
                await DownloadAsync(file, bytes =>
                {
                    received += bytes;
                    reporter.Add(bytes);
                }, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // Der Fortschritt dieses Versuchs zählt nicht.
                reporter.Add(-received);
                if (attempt >= MaxAttempts)
                    throw new UpdateException($"„{file.Path}“ konnte nicht geladen werden: {ex.Message}", ex);
            }

            await Task.Delay(RetryDelay * attempt, cancellationToken);
        }
    }

    private async Task DownloadAsync(ManifestFile file, Action<int> onBytes, CancellationToken cancellationToken)
    {
        var target = GetFullPath(file.Path);
        var temp = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N"));
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;

            await using (var input = await source.OpenReadAsync(UpdateLayout.ObjectPath(file.Sha256), bypassCache: false, cancellationToken))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                        throw new InvalidDataException("Die Datei ist größer als im Manifest angegeben.");

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    onBytes(read);
                }
            }

            if (written != file.Size)
                throw new InvalidDataException($"Unvollständig geladen ({written} von {file.Size} Bytes).");
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                throw new InvalidDataException("Die Prüfsumme stimmt nicht.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private void DeleteEmptyParents(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        while (directory is not null
            && directory.Length > InstallDirectory.Length
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}

/// <param name="Downloads">Dateien, die fehlen oder sich geändert haben.</param>
/// <param name="Deletions">Dateien der alten Version, die es nicht mehr gibt.</param>
public sealed record UpdatePlan(GameManifest Manifest, IReadOnlyList<ManifestFile> Downloads, IReadOnlyList<string> Deletions)
{
    public long DownloadSize => Downloads.Sum(f => f.Size);
    public bool IsUpToDate => Downloads.Count == 0 && Deletions.Count == 0;
}

public readonly record struct UpdateProgress(long BytesDone, long BytesTotal)
{
    /// <summary>Zwischen 0 und 1.</summary>
    public double Fraction => BytesTotal <= 0 ? 1 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

public sealed class UpdateException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Zählt Bytes aus mehreren Downloads gleichzeitig zusammen und meldet höchstens alle 50 ms,
/// damit die Oberfläche nicht mit Meldungen überflutet wird.
/// </summary>
internal sealed class ProgressReporter(IProgress<UpdateProgress>? progress, long total)
{
    private const long IntervalMs = 50;
    private long _done;
    private long _lastReport = Environment.TickCount64 - IntervalMs;

    public void Add(long bytes)
    {
        var done = Interlocked.Add(ref _done, bytes);
        if (progress is null)
            return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReport);
        if (now - last >= IntervalMs && Interlocked.CompareExchange(ref _lastReport, now, last) == last)
            progress.Report(new UpdateProgress(done, total));
    }

    public void Complete() => progress?.Report(new UpdateProgress(Interlocked.Read(ref _done), total));
}
