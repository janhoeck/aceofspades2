using AoS2.Client.World;
using AoS2.Shared.Voxels;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AoS2.Client.Tests
{
    public class ChunkMesherTests
    {
        private const byte Stone = 2;
        private const byte Grass = 4;

        private NativeArray<int> _faceTable;

        [SetUp]
        public void SetUp()
        {
            // Stein: alle Seiten Ebene 0; Gras: oben Ebene 1 mit 2 Varianten, Seiten Ebene 3, unten Ebene 0
            _faceTable = new NativeArray<int>(256 * 6, Allocator.Persistent);
            for (var f = 0; f < 6; f++)
            {
                _faceTable[Stone * 6 + f] = 0 | (1 << 16);
                _faceTable[Grass * 6 + f] = 3 | (1 << 16);
            }
            _faceTable[Grass * 6 + (int)BlockFace.Top] = 1 | (2 << 16);
            _faceTable[Grass * 6 + (int)BlockFace.Bottom] = 0 | (1 << 16);
        }

        [TearDown]
        public void TearDown() => _faceTable.Dispose();

        private (VoxelVertex[] Vertices, int[] Indices) Mesh(VoxelWorld world)
        {
            var size = new int3(world.SizeX, world.SizeY, world.SizeZ);
            var padded = size + 2;
            var buffer = new byte[padded.x * padded.y * padded.z];
            world.CopyRegion(-1, -1, -1, padded.x, padded.y, padded.z, buffer);
            var job = new ChunkMeshJob
            {
                Blocks = new NativeArray<byte>(buffer, Allocator.TempJob),
                Size = size,
                FaceTextures = _faceTable,
                Vertices = new NativeList<VoxelVertex>(Allocator.TempJob),
                Indices = new NativeList<int>(Allocator.TempJob),
            };
            job.Run();
            var result = (job.Vertices.AsArray().ToArray(), job.Indices.AsArray().ToArray());
            job.Blocks.Dispose();
            job.Vertices.Dispose();
            job.Indices.Dispose();
            return result;
        }

        [Test]
        public void SingleBlock_HasSixQuads()
        {
            var world = new VoxelWorld(4, 4, 4);
            world.SetBlock(1, 1, 1, Stone);
            var (vertices, indices) = Mesh(world);
            Assert.AreEqual(6 * 4, vertices.Length);
            Assert.AreEqual(6 * 6, indices.Length);
        }

        [Test]
        public void FlatSlab_IsMergedIntoSixQuads()
        {
            var world = new VoxelWorld(32, 32, 32);
            for (var x = 0; x < 32; x++)
                for (var z = 0; z < 32; z++)
                    world.SetBlock(x, 0, z, Stone);
            var (vertices, _) = Mesh(world);
            Assert.AreEqual(6 * 4, vertices.Length, "Greedy Meshing fasst die ganze Platte je Seite zu einem Viereck zusammen");
        }

        [Test]
        public void BuriedBlocks_HaveNoFaces()
        {
            var world = new VoxelWorld(8, 8, 8);
            for (var x = 2; x < 5; x++)
                for (var y = 2; y < 5; y++)
                    for (var z = 2; z < 5; z++)
                        world.SetBlock(x, y, z, Stone);
            var (vertices, _) = Mesh(world);
            Assert.AreEqual(6 * 4, vertices.Length);
        }

        [Test]
        public void DifferentTextures_AreNotMerged()
        {
            var world = new VoxelWorld(4, 4, 4);
            world.SetBlock(0, 0, 0, Stone);
            world.SetBlock(1, 0, 0, Grass);
            var (vertices, _) = Mesh(world);
            // Oben 2, unten 1 (Gras unten = Stein-Textur), vorne 2, hinten 2, links 1, rechts 1
            Assert.AreEqual((2 + 1 + 2 + 2 + 1 + 1) * 4, vertices.Length);
        }

        [Test]
        public void Triangles_FaceOutward()
        {
            var world = new VoxelWorld(4, 4, 4);
            world.SetBlock(1, 1, 1, Grass);
            world.SetBlock(2, 1, 1, Grass);
            world.SetBlock(1, 2, 1, Stone);
            var (vertices, indices) = Mesh(world);
            for (var i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[indices[i]];
                var b = vertices[indices[i + 1]];
                var c = vertices[indices[i + 2]];
                // Unity zeigt die Seite, auf die cross(b - a, c - a) zeigt; das muss die Normale sein.
                Assert.Greater(math.dot(math.cross(b.Position - a.Position, c.Position - a.Position), a.Normal), 0f);
            }
        }

        [Test]
        public void GrassTop_UsesTopLayerAndVariants()
        {
            var world = new VoxelWorld(4, 4, 4);
            world.SetBlock(1, 1, 1, Grass);
            var (vertices, _) = Mesh(world);
            foreach (var v in vertices)
            {
                if (v.Normal.y > 0) Assert.AreEqual(new float2(1, 2), v.Uv.zw);
                else if (v.Normal.y < 0) Assert.AreEqual(new float2(0, 1), v.Uv.zw);
                else Assert.AreEqual(new float2(3, 1), v.Uv.zw);
            }
        }

        [Test]
        public void SideFaces_TextureVRunsUp()
        {
            var world = new VoxelWorld(4, 4, 4);
            world.SetBlock(1, 1, 1, Stone);
            world.SetBlock(1, 2, 1, Stone);
            var (vertices, _) = Mesh(world);
            foreach (var v in vertices)
                if (v.Normal.y == 0) Assert.AreEqual(v.Position.y, v.Uv.y, 1e-5, "v entspricht der Höhe, damit Grasseiten aufrecht stehen");
        }
    }
}
