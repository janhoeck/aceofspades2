using System;
using System.Collections.Generic;
using System.IO;
using AoS2.Shared.Voxels;
using Unity.Collections;
using UnityEngine;

namespace AoS2.Client.World
{
    /// <summary>
    /// Fasst die Blocktexturen aus blocks.json zu einem Texture2DArray zusammen und baut die Tabelle,
    /// welche Ebene zu welcher Blockseite gehört. Varianten einer Seite liegen als aufeinanderfolgende
    /// Ebenen im Array; der Shader wählt je Block eine davon.
    /// </summary>
    public sealed class BlockTextures : IDisposable
    {
        public const int TextureSize = 16;

        private readonly Color32[] _averageColors;

        private BlockTextures(Texture2DArray array, NativeArray<int> faceTable, Color32[] averageColors)
        {
            Array = array;
            FaceTable = faceTable;
            _averageColors = averageColors;
        }

        /// <summary>Mittlere Farbe eines Blocktyps (Seitenfläche), z. B. für Bruchstücke beim Einschlag.</summary>
        public Color32 AverageColor(byte id) => _averageColors[id];

        public Texture2DArray Array { get; }

        /// <summary>Je Blocktyp und Seite (Id * 6 + Seite): erste Ebene | (Variantenzahl &lt;&lt; 16), siehe <see cref="ChunkMeshJob"/>.</summary>
        public NativeArray<int> FaceTable { get; }

        public static BlockTextures Load(BlockRegistry registry, string folder)
        {
            // Jede Variantenliste (z. B. [grass_top, grass_top_v2]) bekommt einen eigenen Ebenen-Block.
            var groups = new Dictionary<string, int>();
            var layers = new List<string>();
            var table = new NativeArray<int>(256 * BlockFaces.Count, Allocator.Persistent);
            foreach (var block in registry.All)
            {
                for (var f = 0; f < BlockFaces.Count; f++)
                {
                    var names = block.GetTextures((BlockFace)f);
                    var key = string.Join("|", names);
                    if (!groups.TryGetValue(key, out var first))
                    {
                        first = layers.Count;
                        groups.Add(key, first);
                        layers.AddRange(names);
                    }
                    table[block.Id * BlockFaces.Count + f] = first | (names.Count << 16);
                }
            }

            var array = new Texture2DArray(TextureSize, TextureSize, Math.Max(1, layers.Count), TextureFormat.RGBA32, true, false)
            {
                name = "BlockTextures",
                filterMode = FilterMode.Point, // Pixel bleiben scharf
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = 0,
            };
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var layerColors = new Color32[layers.Count];
            for (var i = 0; i < layers.Count; i++)
            {
                var pixels = LoadPixels(image, Path.Combine(folder, layers[i] + ".png"));
                array.SetPixels32(pixels, i, 0);
                layerColors[i] = Average(pixels);
            }
            var averageColors = new Color32[256];
            foreach (var block in registry.All)
            {
                var first = table[block.Id * BlockFaces.Count + (int)BlockFace.PosX] & 0xFFFF;
                averageColors[block.Id] = first < layerColors.Length ? layerColors[first] : new Color32(128, 128, 128, 255);
            }
            UnityEngine.Object.Destroy(image);
            array.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return new BlockTextures(array, table, averageColors);
        }

        private static Color32 Average(Color32[] pixels)
        {
            long r = 0, g = 0, b = 0;
            foreach (var p in pixels)
            {
                r += p.r;
                g += p.g;
                b += p.b;
            }
            var n = Math.Max(1, pixels.Length);
            return new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
        }

        private static Color32[] LoadPixels(Texture2D image, string path)
        {
            if (File.Exists(path) && image.LoadImage(File.ReadAllBytes(path)) && image.width == TextureSize && image.height == TextureSize)
                return image.GetPixels32();

            // Fehlende oder falsche Textur: magenta-schwarzes Schachbrett, damit es sofort auffällt.
            Debug.LogError($"[AoS2] Blocktextur fehlt oder ist nicht {TextureSize} × {TextureSize}: {path}");
            var pixels = new Color32[TextureSize * TextureSize];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = ((i % TextureSize) / 4 + (i / TextureSize) / 4) % 2 == 0 ? new Color32(255, 0, 255, 255) : new Color32(0, 0, 0, 255);
            return pixels;
        }

        public void Dispose()
        {
            if (FaceTable.IsCreated) FaceTable.Dispose();
            if (Array != null) UnityEngine.Object.Destroy(Array);
        }
    }
}
