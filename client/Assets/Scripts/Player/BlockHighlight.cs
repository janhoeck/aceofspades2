using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>Dünner dunkler Rahmen um den Block unter dem Fadenkreuz.</summary>
    public sealed class BlockHighlight : MonoBehaviour
    {
        private const float Grow = 0.004f; // etwas größer als der Block, damit der Rahmen nicht flimmert

        public static BlockHighlight Create(Material material)
        {
            var go = new GameObject("Blockrahmen");
            var mesh = new Mesh { name = "Blockrahmen" };
            var v = new Vector3[8];
            for (var i = 0; i < 8; i++)
                v[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * (1 + 2 * Grow) - Vector3.one * Grow;
            mesh.vertices = v;
            // 12 Kanten des Würfels
            mesh.SetIndices(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 }, MeshTopology.Lines, 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go.AddComponent<BlockHighlight>();
        }

        public void Show(VoxelHit? hit)
        {
            gameObject.SetActive(hit.HasValue);
            if (hit.HasValue) transform.position = new Vector3(hit.Value.X, hit.Value.Y, hit.Value.Z);
        }
    }
}
