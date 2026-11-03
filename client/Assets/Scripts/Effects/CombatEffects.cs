using System.Collections.Generic;
using AoS2.Client.Player;
using AoS2.Client.World;
using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using UnityEngine;

namespace AoS2.Client.Effects
{
    /// <summary>
    /// Einfache Platzhalter-Effekte aus Würfeln: Leuchtspur und Mündungsfeuer, Bruchstücke beim Einschlag (in der Farbe des
    /// Blocks), Explosion (Feuerball, Bruchstücke, Rauch), Zerfall eines getöteten Spielers und die fliegenden Geschosse.
    /// Alles nur Anzeige: Was getroffen ist, entscheidet der Server.
    /// </summary>
    public sealed class CombatEffects : MonoBehaviour
    {
        private const int MaxParticles = 600;

        private Material _material;
        private Mesh _cube;
        private BlockTextures _textures;
        private CombatSettings _combat;
        private MaterialPropertyBlock _props;
        private readonly List<Particle> _particles = new List<Particle>();
        private readonly Stack<Transform> _pool = new Stack<Transform>();
        private readonly Dictionary<int, Transform> _projectiles = new Dictionary<int, Transform>();
        private readonly List<int> _gone = new List<int>();

        private sealed class Particle
        {
            public Transform Transform;
            public MeshRenderer Renderer;
            public Vector3 Velocity;
            public float Gravity;
            public float Age;
            public float Life;
            public Vector3 StartScale;
            public Vector3 EndScale;
            public Color Color;
            public float Emission;
            public float Spin;
        }

        public void Initialize(Material material, BlockTextures textures, CombatSettings combat)
        {
            _material = material;
            _textures = textures;
            _combat = combat;
            _cube = WeaponMeshes.BoxMesh(new[] { (Vector3.one * -0.5f, Vector3.one * 0.5f) }, "Effekt-Würfel");
            _props = new MaterialPropertyBlock();
        }

        public int ActiveParticles => _particles.Count;

        // ---- Effekte ----

        /// <summary>Kurzer heller Blitz an der Mündung.</summary>
        public void MuzzleFlash(Vector3 position, Vector3 direction)
        {
            Spawn(position, Vector3.zero, 0, 0.05f, Vector3.one * 0.18f, Vector3.one * 0.05f, new Color(1f, 0.85f, 0.35f), 1, Quaternion.LookRotation(direction));
        }

        /// <summary>Leuchtspur: ein dünner, langer Würfel von der Mündung bis zum Einschlag, der schnell dünner wird.</summary>
        public void Tracer(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 0.05f) return;
            var p = Spawn((from + to) / 2, Vector3.zero, 0, 0.08f, new Vector3(0.025f, 0.025f, length), new Vector3(0.004f, 0.004f, length),
                new Color(1f, 0.9f, 0.5f), 1, Quaternion.LookRotation(delta));
            if (p != null) p.Spin = 0;
        }

        /// <summary>Einschlag in einen Block: ein paar Bruchstücke in der Farbe des Blocks.</summary>
        public void BlockImpact(Vector3 point, Vector3 normal, byte blockId)
        {
            var color = blockId != 0 && _textures != null ? (Color)_textures.AverageColor(blockId) : Color.gray;
            for (var i = 0; i < 6; i++)
            {
                var v = (normal * 2.5f + Random.insideUnitSphere * 2f);
                Spawn(point + normal * 0.05f, v, 18, Random.Range(0.3f, 0.6f), Vector3.one * 0.07f, Vector3.one * 0.03f, color, 0);
            }
        }

        /// <summary>Treffer an einem Spieler: rote Pixel-Partikel (statt Blut).</summary>
        public void PlayerImpact(Vector3 point, Vector3 direction)
        {
            for (var i = 0; i < 8; i++)
                Spawn(point, (-direction * 1.5f + Random.insideUnitSphere * 2.5f), 14, Random.Range(0.25f, 0.5f), Vector3.one * 0.06f,
                    Vector3.one * 0.02f, new Color(0.85f, 0.1f, 0.1f), 0.3f);
        }

        /// <summary>Explosion: wachsender Feuerball, Bruchstücke im Umkreis und aufsteigender Rauch.</summary>
        public void Explosion(Vector3 center, float radius, VoxelWorldColorSource colors)
        {
            Spawn(center, Vector3.zero, 0, 0.25f, Vector3.one * 0.4f, Vector3.one * radius * 1.1f, new Color(1f, 0.6f, 0.15f), 1);
            Spawn(center, Vector3.zero, 0, 0.12f, Vector3.one * 0.3f, Vector3.one * radius * 0.7f, new Color(1f, 0.95f, 0.6f), 1);
            for (var i = 0; i < 24; i++)
            {
                var dir = Random.onUnitSphere;
                if (dir.y < 0) dir.y = -dir.y * 0.5f;
                var color = colors(center + dir * 0.8f);
                Spawn(center, dir * Random.Range(4f, 10f), 20, Random.Range(0.6f, 1.2f), Vector3.one * Random.Range(0.08f, 0.18f), Vector3.one * 0.04f, color, 0);
            }
            for (var i = 0; i < 10; i++)
                Spawn(center + Random.insideUnitSphere * radius * 0.4f, Vector3.up * Random.Range(0.8f, 2f) + Random.insideUnitSphere * 0.5f, -0.5f,
                    Random.Range(1f, 1.8f), Vector3.one * 0.3f, Vector3.one * Random.Range(0.8f, 1.4f), new Color(0.25f, 0.25f, 0.25f), 0);
        }

        /// <summary>Ein getöteter Spieler zerfällt in Würfel in seinen Farben.</summary>
        public void Shatter(Vector3 feet, Color body, Color legs)
        {
            for (var i = 0; i < 26; i++)
            {
                var height = Random.Range(0.1f, 1.8f);
                var color = height < 0.7f ? legs : height > 1.35f ? new Color(0.93f, 0.75f, 0.6f) : body;
                Spawn(feet + new Vector3(Random.Range(-0.3f, 0.3f), height, Random.Range(-0.3f, 0.3f)),
                    Random.insideUnitSphere * 2.5f + Vector3.up * 1.5f, 16, Random.Range(0.8f, 1.5f), Vector3.one * 0.14f, Vector3.one * 0.06f, color, 0);
            }
        }

        public delegate Color VoxelWorldColorSource(Vector3 point);

        // ---- Geschosse ----

        /// <summary>Einmal pro Bild: Geschosse zeigen, wie die Session sie nachrechnet (Rakete mit Glut und Rauchspur).</summary>
        public void SyncProjectiles(NetProjectiles projectiles)
        {
            foreach (var p in projectiles.All.Values)
            {
                var weapon = _combat.Weapon(p.Weapon);
                if (!_projectiles.TryGetValue(p.Id, out var t))
                {
                    t = Take();
                    var rocket = weapon != null && weapon.Kind == WeaponKind.Rocket;
                    t.localScale = rocket ? new Vector3(0.12f, 0.12f, 0.55f) : Vector3.one * 0.2f;
                    SetColor(t.GetComponent<MeshRenderer>(), rocket ? new Color(0.35f, 0.38f, 0.32f) : new Color(0.2f, 0.32f, 0.15f), 0);
                    _projectiles.Add(p.Id, t);
                }
                var position = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
                var velocity = new Vector3(p.Velocity.X, p.Velocity.Y, p.Velocity.Z);
                if (weapon != null && weapon.Kind == WeaponKind.Rocket)
                {
                    if (velocity.sqrMagnitude > 0.01f) t.rotation = Quaternion.LookRotation(velocity);
                    var back = position - velocity.normalized * 0.35f;
                    if (Random.value < 0.8f) Spawn(back, Vector3.zero, 0, 0.06f, Vector3.one * 0.14f, Vector3.one * 0.05f, new Color(1f, 0.7f, 0.2f), 1);
                    if (Random.value < 0.5f) Spawn(back, Random.insideUnitSphere * 0.3f + Vector3.up * 0.3f, -0.2f, 0.8f, Vector3.one * 0.12f, Vector3.one * 0.4f, new Color(0.7f, 0.7f, 0.7f), 0);
                }
                else
                {
                    t.Rotate(new Vector3(300, 170, 0) * Time.deltaTime * Mathf.Min(velocity.magnitude, 10) / 10);
                }
                t.position = position;
            }
            _gone.Clear();
            foreach (var id in _projectiles.Keys)
                if (!projectiles.All.ContainsKey(id)) _gone.Add(id);
            foreach (var id in _gone)
            {
                Give(_projectiles[id]);
                _projectiles.Remove(id);
            }
        }

        // ---- Teilchen ----

        private Particle Spawn(Vector3 position, Vector3 velocity, float gravity, float life, Vector3 startScale, Vector3 endScale, Color color, float emission,
            Quaternion? rotation = null)
        {
            if (_particles.Count >= MaxParticles) return null;
            var t = Take();
            t.SetPositionAndRotation(position, rotation ?? Random.rotation);
            t.localScale = startScale;
            var p = new Particle
            {
                Transform = t,
                Renderer = t.GetComponent<MeshRenderer>(),
                Velocity = velocity,
                Gravity = gravity,
                Life = life,
                StartScale = startScale,
                EndScale = endScale,
                Color = color,
                Emission = emission,
                Spin = rotation.HasValue ? 0 : Random.Range(-360f, 360f),
            };
            SetColor(p.Renderer, color, emission);
            _particles.Add(p);
            return p;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            for (var i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    Give(p.Transform);
                    _particles.RemoveAt(i);
                    continue;
                }
                p.Velocity.y -= p.Gravity * dt;
                p.Transform.position += p.Velocity * dt;
                if (p.Spin != 0) p.Transform.Rotate(Vector3.one, p.Spin * dt);
                p.Transform.localScale = Vector3.Lerp(p.StartScale, p.EndScale, p.Age / p.Life);
            }
        }

        private void SetColor(MeshRenderer renderer, Color color, float emission)
        {
            _props.SetColor("_BaseColor", color);
            _props.SetFloat("_Emission", emission);
            renderer.SetPropertyBlock(_props);
        }

        private Transform Take()
        {
            if (_pool.Count > 0)
            {
                var t = _pool.Pop();
                t.gameObject.SetActive(true);
                return t;
            }
            var go = new GameObject("Effekt");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _cube;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        private void Give(Transform t)
        {
            t.gameObject.SetActive(false);
            t.rotation = Quaternion.identity;
            _pool.Push(t);
        }

        private void OnDestroy()
        {
            if (_cube != null) Destroy(_cube);
        }
    }
}
