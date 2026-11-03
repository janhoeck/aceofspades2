using System.Globalization;
using AoS2.Shared.Json;
using AoS2.Shared.Voxels;

namespace AoS2.MapConverter;

/// <summary>
/// Zuordnungsdatei Palettenfarbe → Blocktyp. Aufbau:
/// <code>{ "colors": { "#6b8f3a": "grass", "#7a746b": "stone" } }</code>
/// Verglichen wird nur RGB (ohne Alpha). Die Namen müssen in blocks.json stehen.
/// </summary>
public sealed class ColorMapping
{
    private readonly Dictionary<uint, BlockType> _byColor = new();

    public IReadOnlyDictionary<uint, BlockType> Colors => _byColor;

    public static ColorMapping Parse(string json, BlockRegistry registry)
    {
        var mapping = new ColorMapping();
        var colors = JsonParser.Parse(json)["colors"];
        if (colors == null || colors.Kind != JsonKind.Object) throw new FormatException("Zuordnungsdatei: \"colors\" muss ein Objekt sein.");
        foreach (var (key, value) in colors.Properties)
        {
            var rgb = ParseColor(key);
            if (value.Kind != JsonKind.String) throw new FormatException($"Zuordnungsdatei: Wert zu \"{key}\" muss ein Blocktyp-Name sein.");
            if (!registry.TryGet(value.StringValue, out var type)) throw new FormatException($"Zuordnungsdatei: Blocktyp \"{value.StringValue}\" (Farbe {key}) gibt es in blocks.json nicht.");
            if (!mapping._byColor.TryAdd(rgb, type)) throw new FormatException($"Zuordnungsdatei: Farbe {key} steht doppelt drin.");
        }
        return mapping;
    }

    public bool TryGet(uint rgb, out BlockType type) => _byColor.TryGetValue(rgb, out type!);

    public static uint ParseColor(string s)
    {
        var hex = s.StartsWith('#') ? s[1..] : s;
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException($"Zuordnungsdatei: \"{s}\" ist keine Farbe im Format #rrggbb.");
        return rgb;
    }

    public static string FormatColor(uint rgb) => $"#{rgb:x6}";
}
