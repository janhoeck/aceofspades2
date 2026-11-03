using System.Collections.Generic;
using AoS2.Shared.Voxels;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace AoS2.Client.World
{
    /// <summary>
    /// Zeigt die Blockwelt an: ein GameObject mit Mesh pro Chunk (32³). Hört auf Blockänderungen und baut
    /// betroffene Chunks am Ende des Frames neu (bei Änderungen am Rand auch den Nachbarchunk, dessen
    /// Außenfläche sichtbar wird oder verschwindet).
    /// </summary>
    public sealed class WorldRenderer : MonoBehaviour
    {
        private VoxelWorld _world;
        private VoxelMeshBuilder _builder;
        private Material _material;
        private Mesh[] _meshes;
        private readonly HashSet<int> _dirty = new HashSet<int>();

        /// <summary>Wie viele Chunks zuletzt neu gebaut wurden (für Tests und Anzeige).</summary>
        public int LastRebuildCount { get; private set; }

        public int ChunkCount => _meshes?.Length ?? 0;

        public void Initialize(VoxelWorld world, VoxelMeshBuilder builder, Material material)
        {
            _world = world;
            _builder = builder;
            _material = material;
            _meshes = new Mesh[world.ChunksX * world.ChunksY * world.ChunksZ];

            for (var cy = 0; cy < world.ChunksY; cy++)
            for (var cz = 0; cz < world.ChunksZ; cz++)
            for (var cx = 0; cx < world.ChunksX; cx++)
            {
                var go = new GameObject($"Chunk {cx} {cy} {cz}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(cx, cy, cz) * VoxelWorld.ChunkSize;
                var mesh = new Mesh { name = go.name };
                mesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                var index = ChunkIndex(cx, cy, cz);
                _meshes[index] = mesh;
                _dirty.Add(index);
            }

            world.BlockChanged += OnBlockChanged;
            RebuildDirty(); // Erster Aufbau sofort, damit der Spieler nicht ins Leere fällt
        }

        private void OnDestroy()
        {
            if (_world != null) _world.BlockChanged -= OnBlockChanged;
            if (_meshes != null)
                foreach (var m in _meshes) Destroy(m);
        }

        private void LateUpdate() => RebuildDirty();

        private void OnBlockChanged(int x, int y, int z, byte oldId, byte newId)
        {
            int cx = x >> VoxelWorld.ChunkShift, cy = y >> VoxelWorld.ChunkShift, cz = z >> VoxelWorld.ChunkShift;
            MarkDirty(cx, cy, cz);
            int lx = x & VoxelWorld.ChunkMask, ly = y & VoxelWorld.ChunkMask, lz = z & VoxelWorld.ChunkMask;
            const int last = VoxelWorld.ChunkSize - 1;
            if (lx == 0) MarkDirty(cx - 1, cy, cz);
            if (lx == last) MarkDirty(cx + 1, cy, cz);
            if (ly == 0) MarkDirty(cx, cy - 1, cz);
            if (ly == last) MarkDirty(cx, cy + 1, cz);
            if (lz == 0) MarkDirty(cx, cy, cz - 1);
            if (lz == last) MarkDirty(cx, cy, cz + 1);
        }

        private void MarkDirty(int cx, int cy, int cz)
        {
            if (cx < 0 || cy < 0 || cz < 0 || cx >= _world.ChunksX || cy >= _world.ChunksY || cz >= _world.ChunksZ) return;
            _dirty.Add(ChunkIndex(cx, cy, cz));
        }

        private void RebuildDirty()
        {
            LastRebuildCount = _dirty.Count;
            if (_dirty.Count == 0) return;
            const int size = VoxelWorld.ChunkSize;
            foreach (var index in _dirty)
            {
                var cx = index % _world.ChunksX;
                var cz = index / _world.ChunksX % _world.ChunksZ;
                var cy = index / (_world.ChunksX * _world.ChunksZ);
                _builder.Schedule(_world, cx * size, cy * size, cz * size, new int3(size), _meshes[index]);
            }
            _dirty.Clear();
            _builder.CompleteAll();
        }

        private int ChunkIndex(int cx, int cy, int cz) => (cy * _world.ChunksZ + cz) * _world.ChunksX + cx;
    }
}
