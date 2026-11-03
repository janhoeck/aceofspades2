using System.Collections.Generic;
using AoS2.Shared.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AoS2.Client.World
{
    /// <summary>
    /// Zeigt fallende Blockgruppen als eigene Meshes. Die Simulation (wann, wie weit, wo sie landen)
    /// läuft in <see cref="FallingBlocks"/> aus AoS2.Shared; hier wird nur gezeichnet.
    /// </summary>
    public sealed class FallingBlocksView : MonoBehaviour
    {
        private FallingBlocks _falling;
        private VoxelMeshBuilder _builder;
        private Material _material;
        private readonly Dictionary<int, View> _views = new Dictionary<int, View>();

        private sealed class View
        {
            public GameObject GameObject;
            public Mesh Mesh;
            public Vector3 Origin;
            public float PreviousFallen;
        }

        public int ActiveCount => _views.Count;

        public void Initialize(FallingBlocks falling, VoxelMeshBuilder builder, Material material)
        {
            _falling = falling;
            _builder = builder;
            _material = material;
            falling.Started += OnStarted;
            falling.Landed += OnLanded;
        }

        private void OnDestroy()
        {
            if (_falling == null) return;
            _falling.Started -= OnStarted;
            _falling.Landed -= OnLanded;
        }

        /// <summary>Vor jedem Simulationsschritt aufrufen: merkt sich die alte Höhe zum Interpolieren.</summary>
        public void BeforeTick()
        {
            foreach (var g in _falling.Groups)
                if (_views.TryGetValue(g.Id, out var view)) view.PreviousFallen = g.Fallen;
        }

        /// <summary>Zeichnet die Gruppen zwischen zwei Ticks (alpha = 0 … 1).</summary>
        public void UpdateVisuals(float alpha)
        {
            foreach (var g in _falling.Groups)
            {
                if (!_views.TryGetValue(g.Id, out var view)) continue;
                var fallen = Mathf.Lerp(view.PreviousFallen, g.Fallen, alpha);
                view.GameObject.transform.position = view.Origin + Vector3.down * fallen;
            }
        }

        private void OnStarted(FallingGroup group)
        {
            // Begrenzungsquader der Gruppe, darin die Blöcke (mit einem Block Luft als Rand) für den Mesh-Job
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            foreach (var b in group.Blocks)
            {
                minX = math.min(minX, b.X); minY = math.min(minY, b.Y); minZ = math.min(minZ, b.Z);
                maxX = math.max(maxX, b.X); maxY = math.max(maxY, b.Y); maxZ = math.max(maxZ, b.Z);
            }
            var size = new int3(maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
            var padded = size + 2;
            var blocks = new NativeArray<byte>(padded.x * padded.y * padded.z, Allocator.TempJob);
            foreach (var b in group.Blocks)
                blocks[((b.Y - minY + 1) * padded.z + (b.Z - minZ + 1)) * padded.x + (b.X - minX + 1)] = b.Id;

            var go = new GameObject($"Fallende Gruppe {group.Id}");
            go.transform.SetParent(transform, false);
            var mesh = new Mesh { name = go.name };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            var origin = new Vector3(minX, minY, minZ);
            go.transform.position = origin;
            _views[group.Id] = new View { GameObject = go, Mesh = mesh, Origin = origin };

            _builder.Schedule(blocks, size, mesh);
            _builder.CompleteAll();
        }

        private void OnLanded(FallingGroup group, int offset)
        {
            if (!_views.TryGetValue(group.Id, out var view)) return;
            // Die Blöcke stehen jetzt wieder in der Welt; deren Chunks werden am Ende dieses Frames neu gebaut.
            Destroy(view.GameObject);
            Destroy(view.Mesh);
            _views.Remove(group.Id);
        }
    }
}
