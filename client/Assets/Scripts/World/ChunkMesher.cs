using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace AoS2.Client.World
{
    /// <summary>Ein Vertex der Blockwelt, so wie ihn der Shader AoS2/Voxel erwartet.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelVertex
    {
        public float3 Position;
        public float3 Normal;

        /// <summary>xy = Texturkoordinate in Blöcken (wiederholt sich je Block), z = erste Ebene im Texture2DArray, w = Zahl der Varianten.</summary>
        public float4 Uv;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
        };
    }

    /// <summary>
    /// Baut das Mesh eines Blockquaders mit Greedy Meshing, als Burst-Job im Hintergrund.
    ///
    /// Greedy Meshing in Kürze: Für jede der sechs Blickrichtungen wird die Welt Schicht für Schicht
    /// abgetastet. In jeder Schicht entsteht eine Maske der sichtbaren Flächen (Block fest, Nachbar Luft),
    /// beschriftet mit der Textur. Gleich beschriftete Nachbarn werden zu möglichst großen Rechtecken
    /// zusammengefasst – aus einer 32 × 32 großen Grasfläche wird so ein einziges Viereck statt 1024.
    /// Das Texture2DArray und die Texturkoordinaten in Blöcken sorgen dafür, dass die Textur auf dem
    /// großen Viereck trotzdem je Block wiederholt wird.
    /// </summary>
    [BurstCompile]
    public struct ChunkMeshJob : IJob
    {
        /// <summary>Blöcke mit einem Block Rand ringsum: Größe (Size + 2)³, Index (y * PZ + z) * PX + x.</summary>
        [ReadOnly] public NativeArray<byte> Blocks;

        /// <summary>Größe des Bereichs ohne Rand.</summary>
        public int3 Size;

        /// <summary>Je Blocktyp und Seite (Id * 6 + Seite): erste Texturebene | (Variantenzahl &lt;&lt; 16); 0 = nicht sichtbar.</summary>
        [ReadOnly] public NativeArray<int> FaceTextures;

        public NativeList<VoxelVertex> Vertices;
        public NativeList<int> Indices;

        public void Execute()
        {
            var padded = Size + 2;
            var mask = new NativeArray<int>(math.cmax(Size) * math.cmax(Size), Allocator.Temp);

            for (var face = 0; face < 6; face++)
            {
                // Seiten wie AoS2.Shared.Voxels.BlockFace: +X, -X, +Y, -Y, +Z, -Z
                var axis = face >> 1;
                var positive = (face & 1) == 0;
                // Achsen der Fläche: u waagerecht, v bei Seitenwänden nach oben (damit Grasseiten aufrecht stehen)
                var uAxis = axis == 0 ? 2 : 0;
                var vAxis = axis == 1 ? 2 : 1;
                var dir = int3.zero;
                dir[axis] = positive ? 1 : -1;
                var width = Size[uAxis];
                var height = Size[vAxis];

                for (var slice = 0; slice < Size[axis]; slice++)
                {
                    // Maske dieser Schicht
                    for (var j = 0; j < height; j++)
                    {
                        for (var i = 0; i < width; i++)
                        {
                            var p = int3.zero;
                            p[axis] = slice;
                            p[uAxis] = i;
                            p[vAxis] = j;
                            var block = Blocks[Index(p + 1, padded)];
                            var neighbor = Blocks[Index(p + 1 + dir, padded)];
                            mask[j * width + i] = block != 0 && neighbor == 0 ? FaceTextures[block * 6 + face] : 0;
                        }
                    }

                    // Rechtecke zusammenfassen
                    for (var j = 0; j < height; j++)
                    {
                        for (var i = 0; i < width;)
                        {
                            var key = mask[j * width + i];
                            if (key == 0)
                            {
                                i++;
                                continue;
                            }

                            var w = 1;
                            while (i + w < width && mask[j * width + i + w] == key) w++;

                            var h = 1;
                            while (j + h < height)
                            {
                                var rowMatches = true;
                                for (var k = 0; k < w; k++)
                                {
                                    if (mask[(j + h) * width + i + k] == key) continue;
                                    rowMatches = false;
                                    break;
                                }
                                if (!rowMatches) break;
                                h++;
                            }

                            AddQuad(face, axis, uAxis, vAxis, positive, slice, i, j, w, h, key);

                            for (var y = 0; y < h; y++)
                                for (var x = 0; x < w; x++)
                                    mask[(j + y) * width + i + x] = 0;
                            i += w;
                        }
                    }
                }
            }
            mask.Dispose();
        }

        private void AddQuad(int face, int axis, int uAxis, int vAxis, bool positive, int slice, int i, int j, int w, int h, int key)
        {
            var origin = float3.zero;
            origin[axis] = positive ? slice + 1 : slice;
            origin[uAxis] = i;
            origin[vAxis] = j;
            var du = float3.zero;
            du[uAxis] = w;
            var dv = float3.zero;
            dv[vAxis] = h;

            var normal = float3.zero;
            normal[axis] = positive ? 1 : -1;

            // Textur von außen betrachtet nicht gespiegelt: Auf -X und +Z läuft u entgegen der Achse.
            var uSign = face == 1 || face == 4 ? -1f : 1f;
            var u0 = uSign * i;
            var u1 = uSign * (i + w);
            var layer = key & 0xFFFF;
            var variants = key >> 16;

            var start = Vertices.Length;
            Vertices.Add(new VoxelVertex { Position = origin, Normal = normal, Uv = new float4(u0, j, layer, variants) });
            Vertices.Add(new VoxelVertex { Position = origin + du, Normal = normal, Uv = new float4(u1, j, layer, variants) });
            Vertices.Add(new VoxelVertex { Position = origin + du + dv, Normal = normal, Uv = new float4(u1, j + h, layer, variants) });
            Vertices.Add(new VoxelVertex { Position = origin + dv, Normal = normal, Uv = new float4(u0, j + h, layer, variants) });

            // Unity zeigt Dreiecke von der Seite, auf die cross(b - a, c - a) zeigt.
            var flip = math.dot(math.cross(du, dv), normal) < 0;
            if (flip)
            {
                Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 1);
                Indices.Add(start); Indices.Add(start + 3); Indices.Add(start + 2);
            }
            else
            {
                Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
                Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
            }
        }

        private static int Index(int3 p, int3 padded) => (p.y * padded.z + p.z) * padded.x + p.x;
    }
}
