using System.Collections.Generic;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Die eigene Waffe in der Hand (Ich-Perspektive): Platzhalter aus Quadern rechts unten im Bild, mit Rückstoß beim
    /// Schießen, Absenken beim Nachladen und Wechseln. Gehört zur Kamera; nur Anzeige.
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        private static readonly Vector3 Rest = new Vector3(0.28f, -0.26f, 0.5f);

        private readonly Dictionary<int, GameObject> _models = new Dictionary<int, GameObject>();
        private Material _material;
        private int _slot = -1;
        private float _kick;
        private float _lower;

        /// <summary>Mündung der Waffe in Weltkoordinaten (für Mündungsfeuer und Leuchtspur).</summary>
        public Vector3 Muzzle => transform.TransformPoint(new Vector3(0, 0.02f, _slot == 3 ? 0.55f : 0.62f));

        public static WeaponView Create(Transform camera, Material material)
        {
            var go = new GameObject("Waffe (Ich-Perspektive)");
            go.transform.SetParent(camera, false);
            go.transform.localPosition = Rest;
            var view = go.AddComponent<WeaponView>();
            view._material = material;
            return view;
        }

        /// <summary>Zeigt die Waffe auf Platz <paramref name="slot"/>; <paramref name="busy"/> = lädt nach oder wird gewechselt.</summary>
        public void Show(int slot, bool busy, bool visible)
        {
            if (slot != _slot)
            {
                if (_models.TryGetValue(_slot, out var old)) old.SetActive(false);
                if (!_models.TryGetValue(slot, out var model))
                {
                    model = Build(slot);
                    _models.Add(slot, model);
                }
                model.SetActive(true);
                _slot = slot;
                _lower = 1;
            }
            gameObject.SetActive(visible);
            var dt = Time.deltaTime;
            _kick = Mathf.MoveTowards(_kick, 0, dt * 8);
            _lower = Mathf.MoveTowards(_lower, busy ? 1 : 0, dt * 5);
            transform.localPosition = Rest + new Vector3(0, -0.25f * _lower, -0.08f * _kick);
            transform.localRotation = Quaternion.Euler(-8 * _kick + 25 * _lower, 0, 0);
        }

        /// <summary>Rückstoß nach einem Schuss.</summary>
        public void Kick(float amount = 1) => _kick = Mathf.Min(1, _kick + amount);

        private GameObject Build(int slot)
        {
            var boxes = new List<(Vector3, Vector3, Color)>();
            var dark = new Color(0.18f, 0.18f, 0.2f);
            var wood = new Color(0.45f, 0.3f, 0.18f);
            switch (slot)
            {
                case 0: // Spaten
                    boxes.Add((new Vector3(-0.02f, -0.02f, -0.1f), new Vector3(0.02f, 0.02f, 0.4f), wood));
                    boxes.Add((new Vector3(-0.08f, -0.01f, 0.4f), new Vector3(0.08f, 0.01f, 0.6f), new Color(0.55f, 0.55f, 0.58f)));
                    break;
                case 1: // Gewehr
                    boxes.Add((new Vector3(-0.035f, -0.05f, -0.2f), new Vector3(0.035f, 0.04f, 0.25f), dark));
                    boxes.Add((new Vector3(-0.02f, -0.015f, 0.25f), new Vector3(0.02f, 0.02f, 0.62f), dark));
                    boxes.Add((new Vector3(-0.025f, -0.18f, 0.05f), new Vector3(0.025f, -0.05f, 0.12f), new Color(0.25f, 0.25f, 0.27f)));
                    boxes.Add((new Vector3(-0.03f, -0.07f, -0.35f), new Vector3(0.03f, 0.02f, -0.2f), wood));
                    break;
                case 2: // Granate
                    boxes.Add((new Vector3(-0.05f, -0.05f, 0.1f), new Vector3(0.05f, 0.06f, 0.2f), new Color(0.22f, 0.32f, 0.16f)));
                    boxes.Add((new Vector3(-0.015f, 0.06f, 0.13f), new Vector3(0.015f, 0.09f, 0.17f), new Color(0.6f, 0.6f, 0.6f)));
                    break;
                default: // Raketenwerfer
                    boxes.Add((new Vector3(-0.07f, -0.07f, -0.45f), new Vector3(0.07f, 0.07f, 0.55f), new Color(0.3f, 0.34f, 0.26f)));
                    boxes.Add((new Vector3(-0.025f, -0.2f, 0.0f), new Vector3(0.025f, -0.07f, 0.08f), dark));
                    break;
            }
            var root = new GameObject("Platz " + slot);
            root.transform.SetParent(transform, false);
            var props = new MaterialPropertyBlock();
            foreach (var (min, max, color) in boxes)
            {
                var part = new GameObject("Teil");
                part.transform.SetParent(root.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = WeaponMeshes.BoxMesh(new[] { (min, max) }, "Waffenteil");
                var r = part.AddComponent<MeshRenderer>();
                r.sharedMaterial = _material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                props.SetColor("_BaseColor", color);
                r.SetPropertyBlock(props);
            }
            return root;
        }
    }
}
