using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Maps
{
    /// <summary>Eine geladene Map: Blockwelt und Metadaten.</summary>
    public sealed class GameMap
    {
        public GameMap(VoxelWorld world, MapMetadata metadata)
        {
            World = world;
            Metadata = metadata;
        }

        public VoxelWorld World { get; }
        public MapMetadata Metadata { get; }
    }

    /// <summary>
    /// Liest und schreibt das Map-Format <c>.aos2map</c> (alle Zahlen Little Endian):
    /// <list type="number">
    /// <item>Header: Kennung <c>AOS2MAP\0</c> (8 Byte), Format-Version (uint16), Größe X/Y/Z (je int32)</item>
    /// <item>Block-Palette: Anzahl (uint8), dann je Eintrag der Blocktyp-Name (uint16 Länge + UTF-8).
    ///       Map-interne ID 1 ist der erste Eintrag, 0 ist Luft.</item>
    /// <item>Chunk-Daten: Länge (int32), dann Deflate-komprimiert alle Chunks (Reihenfolge y, z, x),
    ///       jeder als Lauflängen: Länge (Varint) + Map-interne ID (1 Byte), bis 32³ Blöcke voll sind.</item>
    /// <item>Metadaten: Länge (int32) + JSON in UTF-8 (siehe <see cref="MapMetadata"/>).</item>
    /// </list>
    /// Die Palette macht die Datei unabhängig von den IDs in blocks.json: Beim Laden werden die Namen
    /// auf die aktuellen IDs abgebildet.
    /// </summary>
    public static class MapFile
    {
        public const ushort FormatVersion = 1;
        public const string Extension = ".aos2map";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AOS2MAP\0");

        public static void Write(Stream stream, VoxelWorld world, MapMetadata metadata, BlockRegistry registry)
        {
            // Palette: nur Blocktypen, die tatsächlich vorkommen, in ID-Reihenfolge.
            var used = new bool[256];
            ForEachChunk(world, (cx, cy, cz) =>
            {
                var data = world.GetChunkData(cx, cy, cz);
                if (data != null) foreach (var b in data) used[b] = true;
            });
            var toMap = new byte[256];
            var palette = new List<string>();
            for (var id = 1; id < 256; id++)
            {
                if (!used[id]) continue;
                var type = registry.Get((byte)id) ?? throw new InvalidDataException($"Die Welt enthält die unbekannte Blocktyp-ID {id}.");
                palette.Add(type.Name);
                toMap[id] = (byte)palette.Count;
            }

            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(world.SizeX);
                w.Write(world.SizeY);
                w.Write(world.SizeZ);

                w.Write((byte)palette.Count);
                foreach (var name in palette) WriteString(w, name);

                var compressed = CompressChunks(world, toMap);
                w.Write(compressed.Length);
                w.Write(compressed);

                var json = Encoding.UTF8.GetBytes((metadata ?? new MapMetadata()).ToJson());
                w.Write(json.Length);
                w.Write(json);
            }
        }

        public static GameMap Read(Stream stream, BlockRegistry registry)
        {
            using (var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                try
                {
                    var magic = r.ReadBytes(Magic.Length);
                    for (var i = 0; i < Magic.Length; i++)
                        if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new InvalidDataException("Keine .aos2map-Datei (Kennung fehlt).");
                    var version = r.ReadUInt16();
                    if (version != FormatVersion) throw new InvalidDataException($"Map-Format-Version {version} wird nicht unterstützt (erwartet {FormatVersion}).");
                    int sx = r.ReadInt32(), sy = r.ReadInt32(), sz = r.ReadInt32();
                    if (sx <= 0 || sy <= 0 || sz <= 0 || sx > 4096 || sy > 1024 || sz > 4096) throw new InvalidDataException($"Ungültige Map-Größe {sx} × {sy} × {sz}.");

                    var paletteCount = r.ReadByte();
                    var toWorld = new byte[256];
                    for (var i = 1; i <= paletteCount; i++)
                    {
                        var name = ReadString(r);
                        if (!registry.TryGet(name, out var type))
                            throw new InvalidDataException($"Die Map nutzt den Blocktyp \"{name}\", den es in blocks.json nicht gibt.");
                        toWorld[i] = type.Id;
                    }

                    var world = new VoxelWorld(sx, sy, sz);
                    var compressedLength = r.ReadInt32();
                    if (compressedLength < 0) throw new InvalidDataException("Ungültige Länge der Chunk-Daten.");
                    var compressed = r.ReadBytes(compressedLength);
                    if (compressed.Length != compressedLength) throw new EndOfStreamException();
                    DecompressChunks(world, compressed, toWorld, paletteCount);

                    var jsonLength = r.ReadInt32();
                    if (jsonLength < 0) throw new InvalidDataException("Ungültige Länge der Metadaten.");
                    var json = r.ReadBytes(jsonLength);
                    if (json.Length != jsonLength) throw new EndOfStreamException();
                    var metadata = MapMetadata.Parse(Encoding.UTF8.GetString(json));
                    return new GameMap(world, metadata);
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException("Die Map-Datei ist unvollständig.");
                }
                catch (FormatException e)
                {
                    throw new InvalidDataException("Map-Metadaten: " + e.Message, e);
                }
            }
        }

        public static GameMap Load(string path, BlockRegistry registry)
        {
            using (var file = File.OpenRead(path))
                return Read(file, registry);
        }

        public static void Save(string path, VoxelWorld world, MapMetadata metadata, BlockRegistry registry)
        {
            using (var file = File.Create(path))
                Write(file, world, metadata, registry);
        }

        private static byte[] CompressChunks(VoxelWorld world, byte[] toMap)
        {
            using (var buffer = new MemoryStream())
            {
                using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var run = new byte[6];
                    ForEachChunk(world, (cx, cy, cz) =>
                    {
                        var data = world.GetChunkData(cx, cy, cz);
                        if (data == null)
                        {
                            WriteRun(deflate, run, VoxelWorld.ChunkVolume, 0);
                            return;
                        }
                        var i = 0;
                        while (i < data.Length)
                        {
                            var value = data[i];
                            var start = i;
                            while (i < data.Length && data[i] == value) i++;
                            WriteRun(deflate, run, i - start, toMap[value]);
                        }
                    });
                }
                return buffer.ToArray();
            }
        }

        private static void DecompressChunks(VoxelWorld world, byte[] compressed, byte[] toWorld, int paletteCount)
        {
            using (var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress))
            using (var input = new BufferedStream(deflate))
            {
                ForEachChunk(world, (cx, cy, cz) =>
                {
                    byte[] data = null;
                    var i = 0;
                    while (i < VoxelWorld.ChunkVolume)
                    {
                        var length = ReadVarint(input);
                        var value = input.ReadByte();
                        if (value < 0) throw new EndOfStreamException();
                        if (length <= 0 || i + length > VoxelWorld.ChunkVolume) throw new InvalidDataException("Beschädigte Chunk-Daten (Lauflänge).");
                        if (value > paletteCount) throw new InvalidDataException($"Beschädigte Chunk-Daten (ID {value} nicht in der Palette).");
                        if (value != 0)
                        {
                            data ??= new byte[VoxelWorld.ChunkVolume];
                            var id = toWorld[value];
                            for (var k = 0; k < length; k++) data[i + k] = id;
                        }
                        i += length;
                    }
                    world.SetChunkData(cx, cy, cz, data);
                });
            }
        }

        private static void ForEachChunk(VoxelWorld world, Action<int, int, int> action)
        {
            for (var cy = 0; cy < world.ChunksY; cy++)
                for (var cz = 0; cz < world.ChunksZ; cz++)
                    for (var cx = 0; cx < world.ChunksX; cx++)
                        action(cx, cy, cz);
        }

        private static void WriteRun(Stream s, byte[] buffer, int length, byte value)
        {
            var n = 0;
            var v = (uint)length;
            while (v >= 0x80)
            {
                buffer[n++] = (byte)(v | 0x80);
                v >>= 7;
            }
            buffer[n++] = (byte)v;
            buffer[n++] = value;
            s.Write(buffer, 0, n);
        }

        private static int ReadVarint(Stream s)
        {
            var result = 0;
            for (var shift = 0; shift < 28; shift += 7)
            {
                var b = s.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new InvalidDataException("Beschädigte Chunk-Daten (Varint zu lang).");
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        private static string ReadString(BinaryReader r)
        {
            var length = r.ReadUInt16();
            var bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
