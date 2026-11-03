namespace AoS2.MapConverter;

/// <summary>Kommandozeile des Konverters.</summary>
public sealed record ConverterOptions(
    string Input,
    string Mapping,
    string Output,
    string Blocks,
    string? Metadata,
    (int X, int Y, int Z)? Size)
{
    public const string Usage = """
        Aufruf:
          dotnet run --project tools/AoS2.MapConverter -- <map.vox> --mapping <zuordnung.json> [Optionen]

        Optionen:
          --mapping <datei>   Zuordnung Palettenfarbe → Blocktyp (Pflicht)
          --meta <datei>      Metadaten (Name, Zonen, Spawnpunkte) als JSON, wird in die Map eingebettet
          --blocks <datei>    Blocktypen (Standard: data/blocks.json)
          --size <x,y,z>      Feste Map-Größe (x, Höhe, z); Standard: Ausdehnung aller Voxel
          -o, --output <datei> Ausgabe (Standard: <map>.aos2map neben der Eingabe)
        """;

    public static ConverterOptions? Parse(string[] args, out string? error)
    {
        error = null;
        string? input = null, mapping = null, output = null, metadata = null, blocks = "data/blocks.json";
        (int, int, int)? size = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help") return null;
            if (!arg.StartsWith('-'))
            {
                if (input != null)
                {
                    error = $"Mehr als eine Eingabedatei: {arg}";
                    return null;
                }
                input = arg;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                error = $"{arg} braucht einen Wert.";
                return null;
            }
            var value = args[++i];
            switch (arg)
            {
                case "--mapping": mapping = value; break;
                case "--meta": metadata = value; break;
                case "--blocks": blocks = value; break;
                case "-o" or "--output": output = value; break;
                case "--size":
                    var parts = value.Split(',');
                    if (parts.Length != 3 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) || !int.TryParse(parts[2], out var z) || x <= 0 || y <= 0 || z <= 0)
                    {
                        error = $"--size erwartet drei positive Zahlen wie 128,64,128, nicht \"{value}\".";
                        return null;
                    }
                    size = (x, y, z);
                    break;
                default:
                    error = $"Unbekannte Option {arg}.";
                    return null;
            }
        }

        if (input == null)
        {
            error = args.Length == 0 ? null : "Keine .vox-Datei angegeben.";
            return null;
        }
        if (mapping == null)
        {
            error = "--mapping fehlt.";
            return null;
        }
        output ??= Path.ChangeExtension(input, ".aos2map");
        return new ConverterOptions(input, mapping, output, blocks, metadata, size);
    }
}
