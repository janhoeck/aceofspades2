using System.Collections.Generic;
using AoS2.Shared.Voxels;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace AoS2.Client.World
{
    /// <summary>
    /// Plant Mesh-Jobs für Blockbereiche und überträgt die Ergebnisse in Unity-Meshes.
    /// Mehrere Jobs laufen gleichzeitig auf allen Prozessorkernen; <see cref="CompleteAll"/> wartet auf sie.
    /// </summary>
    public sealed class VoxelMeshBuilder
    {
        private const MeshUpdateFlags Flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;

        private readonly NativeArray<int> _faceTable;
        private readonly List<Pending> _pending = new List<Pending>();
        private byte[] _buffer = new byte[0];

        private struct Pending
        {
            public JobHandle Handle;
            public ChunkMeshJob Job;
            public Mesh Target;
            public Bounds Bounds;
        }

        public VoxelMeshBuilder(NativeArray<int> faceTable)
        {
            _faceTable = faceTable;
        }

        /// <summary>Plant einen Job für den Quader ab (x0, y0, z0) mit der Größe size; Ergebnis landet in <paramref name="target"/>.</summary>
        public void Schedule(VoxelWorld world, int x0, int y0, int z0, int3 size, Mesh target)
        {
            var padded = size + 2;
            var volume = padded.x * padded.y * padded.z;
            if (_buffer.Length < volume) _buffer = new byte[volume];
            world.CopyRegion(x0 - 1, y0 - 1, z0 - 1, padded.x, padded.y, padded.z, _buffer);
            var blocks = new NativeArray<byte>(volume, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            NativeArray<byte>.Copy(_buffer, blocks, volume);
            Schedule(blocks, size, target);
        }

        /// <summary>Plant einen Job für bereits gefüllte Blockdaten (mit Rand); der Builder gibt sie danach frei.</summary>
        public void Schedule(NativeArray<byte> paddedBlocks, int3 size, Mesh target)
        {
            var job = new ChunkMeshJob
            {
                Blocks = paddedBlocks,
                Size = size,
                FaceTextures = _faceTable,
                Vertices = new NativeList<VoxelVertex>(1024, Allocator.TempJob),
                Indices = new NativeList<int>(1536, Allocator.TempJob),
            };
            _pending.Add(new Pending
            {
                Job = job,
                Handle = job.Schedule(),
                Target = target,
                Bounds = new Bounds(new Vector3(size.x, size.y, size.z) * 0.5f, new Vector3(size.x, size.y, size.z)),
            });
        }

        public int PendingCount => _pending.Count;

        /// <summary>Wartet auf alle geplanten Jobs und schreibt ihre Meshes.</summary>
        public void CompleteAll()
        {
            if (_pending.Count == 0) return;
            JobHandle.ScheduleBatchedJobs();
            foreach (var p in _pending)
            {
                p.Handle.Complete();
                Apply(p.Target, p.Job.Vertices, p.Job.Indices, p.Bounds);
                p.Job.Blocks.Dispose();
                p.Job.Vertices.Dispose();
                p.Job.Indices.Dispose();
            }
            _pending.Clear();
        }

        private static void Apply(Mesh mesh, NativeList<VoxelVertex> vertices, NativeList<int> indices, Bounds bounds)
        {
            mesh.Clear();
            if (vertices.Length == 0) return;
            mesh.SetVertexBufferParams(vertices.Length, VoxelVertex.Layout);
            mesh.SetVertexBufferData(vertices.AsArray(), 0, 0, vertices.Length, 0, Flags);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices.AsArray(), 0, 0, indices.Length, Flags);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), Flags);
            mesh.bounds = bounds;
        }
    }
}
