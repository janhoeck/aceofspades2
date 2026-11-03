using System.Collections.Generic;
using AoS2.Client.Player;
using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client.World
{
    /// <summary>
    /// Risse in beschädigten Blöcken: ein etwas größerer Würfel mit Riss-Textur über dem Block, vier Stufen je nach Schaden.
    /// Die Riss-Texturen (16 × 16, Platzhalter) werden beim Start aus einem festen Zufallsmuster erzeugt.
    /// </summary>
    public sealed class BlockCracksView : MonoBehaviour
    {
        public const int Stages = 4;
        private const float Grow = 0.006f;

        private readonly Material[] _materials = new Material[Stages];
        private readonly Texture2D[] _textures = new Texture2D[Stages];
        private readonly Dictionary<BlockPos, (GameObject Go, int Stage)> _shown = new Dictionary<BlockPos, (GameObject, int)>();
        private readonly List<BlockPos> _gone = new List<BlockPos>();
        private Mesh _mesh;

        public int Count => _shown.Count;

        public void Initialize(Shader shader)
        {
            _mesh = WeaponMeshes.BoxMesh(new[] { (Vector3.one * -Grow, Vector3.one * (1 + Grow)) }, "Risse");
            for (var s = 0; s < Stages; s++)
            {
                _textures[s] = CrackTexture(s);
                _materials[s] = new Material(shader) { name = "Risse " + (s + 1) };
                _materials[s].SetTexture("_BaseMap", _textures[s]);
                _materials[s].SetColor("_BaseColor", new Color(0.08f, 0.07f, 0.06f, 1));
            }
        }

        /// <summary>Stufe 0 … 3 aus dem Schaden 1 … 255.</summary>
        public static int StageOf(byte level) => Mathf.Min(Stages - 1, level * Stages / 256);

        /// <summary>Gleicht die angezeigten Risse mit dem Blockschaden der Session ab (nur Änderungen).</summary>
        public void Sync(IReadOnlyDictionary<BlockPos, byte> damage)
        {
            foreach (var pair in damage)
            {
                var stage = StageOf(pair.Value);
                if (_shown.TryGetValue(pair.Key, out var shown))
                {
                    if (shown.Stage == stage) continue;
                    shown.Go.GetComponent<MeshRenderer>().sharedMaterial = _materials[stage];
                    _shown[pair.Key] = (shown.Go, stage);
                    continue;
                }
                var go = new GameObject($"Riss {pair.Key}");
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(pair.Key.X, pair.Key.Y, pair.Key.Z);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _materials[stage];
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _shown.Add(pair.Key, (go, stage));
            }
            _gone.Clear();
            foreach (var key in _shown.Keys)
                if (!damage.ContainsKey(key)) _gone.Add(key);
            foreach (var key in _gone)
            {
                Destroy(_shown[key].Go);
                _shown.Remove(key);
            }
        }

        /// <summary>Riss-Muster: von der Mitte aus verzweigte Linien, mit jeder Stufe mehr und länger.</summary>
        private static Texture2D CrackTexture(int stage)
        {
            const int size = 16;
            var pixels = new Color32[size * size];
            var random = new System.Random(1234); // fest: jede Stufe enthält die Risse der vorigen
            var lines = 2 + stage * 2;
            for (var l = 0; l < lines; l++)
            {
                float x = 7.5f + (float)random.NextDouble(), y = 7.5f + (float)random.NextDouble();
                var angle = random.NextDouble() * Mathf.PI * 2;
                var length = 3 + stage * 2 + random.Next(3);
                for (var step = 0; step < length; step++)
                {
                    angle += (random.NextDouble() - 0.5) * 1.2;
                    x += (float)System.Math.Cos(angle);
                    y += (float)System.Math.Sin(angle);
                    var px = Mathf.FloorToInt(x);
                    var py = Mathf.FloorToInt(y);
                    if (px < 0 || py < 0 || px >= size || py >= size) break;
                    pixels[py * size + px] = new Color32(255, 255, 255, 255);
                }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Risse " + (stage + 1) };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            foreach (var m in _materials) if (m != null) Destroy(m);
            foreach (var t in _textures) if (t != null) Destroy(t);
        }
    }
}
