using AoS2.Updates;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace AoS2.ManifestBuilder;

public static class ReleaseBuilder
{
    /// <summary>Hasht alle Dateien des Builds und erstellt daraus das Manifest.</summary>
    public static async Task<GameManifest> CreateManifestAsync(BuilderOptions options, CancellationToken cancellationToken = default)
    {
        var input = new DirectoryInfo(options.InputDirectory);
        if (!input.Exists)
            throw new DirectoryNotFoundException($"Build-Ordner „{input.FullName}“ nicht gefunden.");

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*");
        matcher.AddExcludePatterns(options.Excludes);

        // Feste Reihenfolge, damit gleiche Builds ein identisches Manifest ergeben.
        var paths = matcher.Execute(new DirectoryInfoWrapper(input)).Files
            .Select(match => match.Path.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var files = new List<ManifestFile>(paths.Count);
        foreach (var path in paths)
        {
            var fullPath = Path.Combine(input.FullName, path);
            var sha256 = await FileHasher.ComputeSha256Async(fullPath, cancellationToken);
            files.Add(new ManifestFile(path, new FileInfo(fullPath).Length, sha256));
        }

        var manifest = new GameManifest
        {
            GameVersion = options.GameVersion,
            Executable = options.Executable,
            Files = files,
        };
        ManifestValidator.Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// Schreibt Objekte und Manifeste in den Release-Ordner (Aufbau siehe <see cref="UpdateLayout"/>).
    /// Gibt die Zahl neu geschriebener Objekte zurück; vorhandene werden übersprungen.
    /// </summary>
    public static async Task<int> WriteReleaseAsync(GameManifest manifest, BuilderOptions options, CancellationToken cancellationToken = default)
    {
        var written = 0;
        foreach (var file in manifest.Files)
        {
            var target = Path.Combine(options.OutputDirectory, UpdateLayout.ObjectPath(file.Sha256));
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == file.Size)
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(options.InputDirectory, file.Path), target, overwrite: true);
            written++;
        }

        // Das Archiv-Manifest zuerst, das aktuelle zuletzt: Wer hochlädt, hält dieselbe Reihenfolge ein.
        await WriteManifestAsync(manifest, Path.Combine(options.OutputDirectory, UpdateLayout.VersionedManifestPath(manifest.GameVersion)), cancellationToken);
        await WriteManifestAsync(manifest, Path.Combine(options.OutputDirectory, UpdateLayout.ManifestPath), cancellationToken);
        return written;
    }

    private static async Task WriteManifestAsync(GameManifest manifest, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await ManifestSerializer.WriteAsync(manifest, stream, cancellationToken);
    }
}
