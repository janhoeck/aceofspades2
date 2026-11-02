using AoS2.ManifestBuilder;
using AoS2.Updates;

var options = BuilderOptions.Parse(args, out var error);
if (options is null)
{
    if (!string.IsNullOrEmpty(error))
        Console.Error.WriteLine($"Fehler: {error}{Environment.NewLine}");
    Console.WriteLine(BuilderOptions.Usage);
    return string.IsNullOrEmpty(error) ? 0 : 2;
}

try
{
    Console.WriteLine($"Hashe Dateien in {Path.GetFullPath(options.InputDirectory)} …");
    var manifest = await ReleaseBuilder.CreateManifestAsync(options);
    var written = await ReleaseBuilder.WriteReleaseAsync(manifest, options);

    Console.WriteLine($"Spielversion:  {manifest.GameVersion}");
    Console.WriteLine($"Dateien:       {manifest.Files.Count} ({manifest.TotalSize / 1024.0 / 1024.0:0.0} MB)");
    Console.WriteLine($"Neue Objekte:  {written}");
    Console.WriteLine($"Manifest:      {Path.GetFullPath(Path.Combine(options.OutputDirectory, UpdateLayout.ManifestPath))}");
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidManifestException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}
