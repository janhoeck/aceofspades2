using System;
using System.Collections.Generic;
using AoS2.Shared.Json;

namespace AoS2.Shared.Voxels
{
    /// <summary>
    /// Alle Blocktypen, gelesen aus <c>data/blocks.json</c>. ID 0 ist immer Luft.
    /// </summary>
    public sealed class BlockRegistry
    {
        public const byte Air = 0;

        private readonly BlockType[] _byId = new BlockType[256];
        private readonly Dictionary<string, BlockType> _byName = new Dictionary<string, BlockType>(StringComparer.Ordinal);
        private readonly List<BlockType> _all = new List<BlockType>();

        public BlockRegistry(IEnumerable<BlockType> types)
        {
            foreach (var t in types)
            {
                if (t.Id == Air) throw new FormatException($"Blocktyp \"{t.Name}\": ID 0 ist für Luft reserviert.");
                if (_byId[t.Id] != null) throw new FormatException($"Blocktyp-ID {t.Id} ist doppelt vergeben ({_byId[t.Id].Name}, {t.Name}).");
                if (_byName.ContainsKey(t.Name)) throw new FormatException($"Blocktyp-Name \"{t.Name}\" ist doppelt vergeben.");
                _byId[t.Id] = t;
                _byName.Add(t.Name, t);
                _all.Add(t);
            }
            _all.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        /// <summary>Alle Blocktypen, nach ID sortiert.</summary>
        public IReadOnlyList<BlockType> All => _all;

        /// <summary>Blocktyp zu einer ID oder <c>null</c> (Luft oder unbekannt).</summary>
        public BlockType Get(byte id) => _byId[id];

        public bool TryGet(string name, out BlockType type) => _byName.TryGetValue(name, out type);

        public BlockType Get(string name) =>
            TryGet(name, out var t) ? t : throw new KeyNotFoundException($"Unbekannter Blocktyp \"{name}\".");

        public bool IsIndestructible(byte id) => _byId[id] != null && _byId[id].Indestructible;

        /// <summary>
        /// Liest blocks.json. Aufbau je Block:
        /// <c>{ "id": 4, "name": "grass", "displayName": "Gras", "health": 50,
        ///      "textures": { "top": ["block_grass_top", "block_grass_top_v2"], "side": "block_grass_side", "bottom": "block_dirt" } }</c>.
        /// Texturschlüssel: <c>all</c>, <c>side</c> (alle vier Seiten), <c>top</c>, <c>bottom</c>,
        /// einzeln <c>posX</c>/<c>negX</c>/<c>posZ</c>/<c>negZ</c>. Spezifischere Schlüssel gewinnen.
        /// </summary>
        public static BlockRegistry Parse(string json)
        {
            var root = JsonParser.Parse(json);
            var blocks = root["blocks"];
            if (blocks == null || blocks.Kind != JsonKind.Array) throw new FormatException("blocks.json: \"blocks\" muss ein Array sein.");

            var types = new List<BlockType>();
            foreach (var b in blocks.Items)
            {
                var name = b.RequireString("name");
                try
                {
                    var id = b.RequireInt("id");
                    if (id < 1 || id > 255) throw new FormatException("\"id\" muss zwischen 1 und 255 liegen.");
                    types.Add(new BlockType(
                        (byte)id,
                        name,
                        b.GetString("displayName", name),
                        ParseTextures(b["textures"]),
                        b.GetInt("health", 100),
                        b.GetBool("indestructible", false),
                        b.GetBool("buildable", false)));
                }
                catch (FormatException e)
                {
                    throw new FormatException($"blocks.json, Block \"{name}\": {e.Message}", e);
                }
            }
            return new BlockRegistry(types);
        }

        private static string[][] ParseTextures(JsonValue textures)
        {
            if (textures == null || textures.Kind != JsonKind.Object) throw new FormatException("\"textures\" fehlt.");

            var faces = new string[BlockFaces.Count][];
            void Apply(string key, params BlockFace[] targets)
            {
                var v = textures[key];
                if (v == null) return;
                var names = ReadNames(v, key);
                foreach (var f in targets) faces[(int)f] = names;
            }

            // Vom Allgemeinen zum Speziellen, damit spezifische Schlüssel gewinnen.
            Apply("all", BlockFace.PosX, BlockFace.NegX, BlockFace.Top, BlockFace.Bottom, BlockFace.PosZ, BlockFace.NegZ);
            Apply("side", BlockFace.PosX, BlockFace.NegX, BlockFace.PosZ, BlockFace.NegZ);
            Apply("top", BlockFace.Top);
            Apply("bottom", BlockFace.Bottom);
            Apply("posX", BlockFace.PosX);
            Apply("negX", BlockFace.NegX);
            Apply("posZ", BlockFace.PosZ);
            Apply("negZ", BlockFace.NegZ);

            for (var i = 0; i < faces.Length; i++)
                if (faces[i] == null) throw new FormatException($"Keine Textur für Seite {(BlockFace)i}.");
            return faces;
        }

        private static string[] ReadNames(JsonValue v, string key)
        {
            if (v.Kind == JsonKind.String) return new[] { v.StringValue };
            if (v.Kind == JsonKind.Array && v.Items.Count > 0)
            {
                var names = new string[v.Items.Count];
                for (var i = 0; i < names.Length; i++)
                {
                    if (v.Items[i].Kind != JsonKind.String) throw new FormatException($"\"textures.{key}\" darf nur Texte enthalten.");
                    names[i] = v.Items[i].StringValue;
                }
                return names;
            }
            throw new FormatException($"\"textures.{key}\" muss ein Text oder eine nicht leere Liste sein.");
        }
    }
}
