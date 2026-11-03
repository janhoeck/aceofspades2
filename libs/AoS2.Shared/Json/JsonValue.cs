using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AoS2.Shared.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// Ein JSON-Wert als Baum. Schlanker Ersatz für System.Text.Json/Newtonsoft, weil der gemeinsame Code
    /// in Unity ohne zusätzliche Pakete laufen muss. Reicht für Spieldaten (blocks.json, Map-Metadaten).
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        private readonly List<JsonValue> _items;
        private readonly List<KeyValuePair<string, JsonValue>> _properties;

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Array) _items = new List<JsonValue>();
            if (kind == JsonKind.Object) _properties = new List<KeyValuePair<string, JsonValue>>();
        }

        public JsonKind Kind { get; }
        public bool BoolValue { get; private set; }
        public double NumberValue { get; private set; }
        public string StringValue { get; private set; }

        public bool IsNull => Kind == JsonKind.Null;

        /// <summary>Elemente eines Arrays (leer bei anderen Arten).</summary>
        public IReadOnlyList<JsonValue> Items => (IReadOnlyList<JsonValue>)_items ?? Array.Empty<JsonValue>();

        /// <summary>Eigenschaften eines Objekts in Dateireihenfolge (leer bei anderen Arten).</summary>
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Properties =>
            (IReadOnlyList<KeyValuePair<string, JsonValue>>)_properties ?? Array.Empty<KeyValuePair<string, JsonValue>>();

        /// <summary>Eigenschaft eines Objekts oder <c>null</c>, wenn es sie nicht gibt.</summary>
        public JsonValue this[string name]
        {
            get
            {
                if (_properties == null) return null;
                foreach (var p in _properties)
                    if (p.Key == name) return p.Value;
                return null;
            }
        }

        public static JsonValue Bool(bool value) => new JsonValue(JsonKind.Bool) { BoolValue = value };
        public static JsonValue Number(double value) => new JsonValue(JsonKind.Number) { NumberValue = value };
        public static JsonValue String(string value) => value == null ? Null : new JsonValue(JsonKind.String) { StringValue = value };
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);
        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);

        public JsonValue Add(JsonValue item)
        {
            if (_items == null) throw new InvalidOperationException("Kein JSON-Array.");
            _items.Add(item ?? Null);
            return this;
        }

        /// <summary>Setzt eine Eigenschaft (ersetzt eine vorhandene gleichen Namens).</summary>
        public JsonValue Set(string name, JsonValue value)
        {
            if (_properties == null) throw new InvalidOperationException("Kein JSON-Objekt.");
            for (var i = 0; i < _properties.Count; i++)
            {
                if (_properties[i].Key != name) continue;
                _properties[i] = new KeyValuePair<string, JsonValue>(name, value ?? Null);
                return this;
            }
            _properties.Add(new KeyValuePair<string, JsonValue>(name, value ?? Null));
            return this;
        }

        public JsonValue Set(string name, string value) => Set(name, String(value));
        public JsonValue Set(string name, double value) => Set(name, Number(value));
        public JsonValue Set(string name, bool value) => Set(name, Bool(value));

        // ---- Bequeme Zugriffe mit Prüfung; Fehler nennen den Pfad, damit kaputte Daten leicht zu finden sind ----

        public string GetString(string name, string fallback = null)
        {
            var v = this[name];
            if (v == null || v.IsNull) return fallback;
            if (v.Kind != JsonKind.String) throw new FormatException($"\"{name}\" muss ein Text sein.");
            return v.StringValue;
        }

        public string RequireString(string name) =>
            GetString(name) ?? throw new FormatException($"\"{name}\" fehlt.");

        public double GetNumber(string name, double fallback)
        {
            var v = this[name];
            if (v == null || v.IsNull) return fallback;
            if (v.Kind != JsonKind.Number) throw new FormatException($"\"{name}\" muss eine Zahl sein.");
            return v.NumberValue;
        }

        public double RequireNumber(string name)
        {
            var v = this[name];
            if (v == null || v.Kind != JsonKind.Number) throw new FormatException($"\"{name}\" fehlt oder ist keine Zahl.");
            return v.NumberValue;
        }

        public int GetInt(string name, int fallback) => ToInt(GetNumber(name, fallback), name);
        public int RequireInt(string name) => ToInt(RequireNumber(name), name);
        public float GetFloat(string name, float fallback) => (float)GetNumber(name, fallback);

        public bool GetBool(string name, bool fallback)
        {
            var v = this[name];
            if (v == null || v.IsNull) return fallback;
            if (v.Kind != JsonKind.Bool) throw new FormatException($"\"{name}\" muss true oder false sein.");
            return v.BoolValue;
        }

        public int AsInt(string what)
        {
            if (Kind != JsonKind.Number) throw new FormatException($"{what} muss eine Zahl sein.");
            return ToInt(NumberValue, what);
        }

        private static int ToInt(double value, string what)
        {
            if (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue)
                throw new FormatException($"\"{what}\" muss eine ganze Zahl sein.");
            return (int)value;
        }

        // ---- Schreiben ----

        public override string ToString() => ToJson(false);

        public string ToJson(bool indented)
        {
            var sb = new StringBuilder();
            Write(sb, indented ? 0 : -1);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int indent)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(BoolValue ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(FormatNumber(NumberValue)); break;
                case JsonKind.String: WriteString(sb, StringValue); break;
                case JsonKind.Array:
                    // Kurze Zahlen-Arrays (Koordinaten) bleiben in einer Zeile.
                    var inline = indent < 0 || _items.TrueForAll(i => i.Kind != JsonKind.Array && i.Kind != JsonKind.Object);
                    sb.Append('[');
                    for (var i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(inline && indent >= 0 ? ", " : ",");
                        if (!inline) NewLine(sb, indent + 1);
                        _items[i].Write(sb, indent < 0 ? -1 : indent + 1);
                    }
                    if (!inline && _items.Count > 0) NewLine(sb, indent);
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (var i = 0; i < _properties.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indent >= 0) NewLine(sb, indent + 1);
                        WriteString(sb, _properties[i].Key);
                        sb.Append(indent >= 0 ? ": " : ":");
                        _properties[i].Value.Write(sb, indent < 0 ? -1 : indent + 1);
                    }
                    if (indent >= 0 && _properties.Count > 0) NewLine(sb, indent);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new FormatException("JSON kennt kein NaN/Unendlich.");
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15) return ((long)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
