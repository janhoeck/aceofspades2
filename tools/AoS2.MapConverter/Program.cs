using AoS2.MapConverter;
using AoS2.Shared.Maps;
using AoS2.Shared.Voxels;

var options = ConverterOptions.Parse(args, out var error);
if (options is null)
{
    if (!string.IsNullOrEmpty(error))
        Console.Error.WriteLine($"Fehler: {error}{Environment.NewLine}");
    Console.WriteLine(ConverterOptions.Usage);
    return string.IsNullOrEmpty(error) ? 0 : 2;
}

try
{
    var registry = BlockRegistry.Parse(File.ReadAllText(options.Blocks));
    var mapping = ColorMapping.Parse(File.ReadAllText(options.Mapping), registry);
    var metadata = options.Metadata is null ? new MapMetadata { Name = Path.GetFileNameWithoutExtension(options.Input) } : MapMetadata.Parse(File.ReadAllText(options.Metadata));

    var vox = VoxFile.Load(options.Input);
    var world = VoxImporter.Convert(vox, mapping, options.Size);
    MapChecks.Validate(world, metadata);

    MapFile.Save(options.Output, world, metadata, registry);

    Console.WriteLine($"Map:     {metadata.Name}");
    Console.WriteLine($"Größe:   {world.SizeX} × {world.SizeY} × {world.SizeZ} (x × Höhe × z)");
    Console.WriteLine($"Modelle: {vox.GetInstances().Count}");
    Console.WriteLine($"Datei:   {Path.GetFullPath(options.Output)} ({new FileInfo(options.Output).Length / 1024.0:0.0} KB)");
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ConversionException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}
