using System.Collections.Generic;
using AoS2.Shared.Combat;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Die eigene Waffe in der Hand (Ich-Perspektive): Platzhalter aus Quadern rechts unten im Bild, je Waffe eine eigene Form
    /// (<c>model</c> in weapons.json), mit Rückstoß beim Schießen, Absenken beim Nachladen und Wechseln. Gehört zur Kamera;
    /// nur Anzeige. Die fertigen Modelle aus Blockbench kommen in M9.
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        private static readonly Vector3 Rest = new Vector3(0.28f, -0.26f, 0.5f);

        /// <summary>Platzhalter für den Spaten (Platz 1).</summary>
        public const string SpadeModel = "shovel";

        private readonly Dictionary<string, GameObject> _models = new Dictionary<string, GameObject>();
        private Material _material;
        private string _model;
        private float _kick;
        private float _lower;

        /// <summary>Mündung der Waffe in Weltkoordinaten (für Mündungsfeuer und Leuchtspur).</summary>
        public Vector3 Muzzle => transform.TransformPoint(new Vector3(0, 0.02f, MuzzleDistance(_model)));

        public static WeaponView Create(Transform camera, Material material)
        {
            var go = new GameObject("Waffe (Ich-Perspektive)");
            go.transform.SetParent(camera, false);
            go.transform.localPosition = Rest;
            var view = go.AddComponent<WeaponView>();
            view._material = material;
            return view;
        }

        /// <summary>Zeigt <paramref name="weapon"/> (<c>null</c> = Spaten); <paramref name="busy"/> = lädt nach oder wird gewechselt.</summary>
        public void Show(WeaponSettings weapon, bool busy, bool visible)
        {
            var model = weapon?.Model ?? SpadeModel;
            if (model != _model)
            {
                if (_model != null && _models.TryGetValue(_model, out var old)) old.SetActive(false);
                if (!_models.TryGetValue(model, out var shown))
                {
                    shown = Build(model);
                    _models.Add(model, shown);
                }
                shown.SetActive(true);
                _model = model;
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

        private static float MuzzleDistance(string model)
        {
            switch (model)
            {
                case "rocket": return 0.55f;
                case "sniper": return 0.8f;
                case "mp5": return 0.42f;
                case "pistol": return 0.3f;
                default: return 0.62f;
            }
        }

        private GameObject Build(string model)
        {
            var boxes = new List<(Vector3, Vector3, Color)>();
            var dark = new Color(0.18f, 0.18f, 0.2f);
            var metal = new Color(0.25f, 0.25f, 0.27f);
            var wood = new Color(0.45f, 0.3f, 0.18f);
            switch (model)
            {
                case SpadeModel:
                    boxes.Add((new Vector3(-0.02f, -0.02f, -0.1f), new Vector3(0.02f, 0.02f, 0.4f), wood));
                    boxes.Add((new Vector3(-0.08f, -0.01f, 0.4f), new Vector3(0.08f, 0.01f, 0.6f), new Color(0.55f, 0.55f, 0.58f)));
                    break;
                case "grenade":
                    boxes.Add((new Vector3(-0.05f, -0.05f, 0.1f), new Vector3(0.05f, 0.06f, 0.2f), new Color(0.22f, 0.32f, 0.16f)));
                    boxes.Add((new Vector3(-0.015f, 0.06f, 0.13f), new Vector3(0.015f, 0.09f, 0.17f), new Color(0.6f, 0.6f, 0.6f)));
                    break;
                case "rocket": // Raketenwerfer: dickes Rohr
                    boxes.Add((new Vector3(-0.07f, -0.07f, -0.45f), new Vector3(0.07f, 0.07f, 0.55f), new Color(0.3f, 0.34f, 0.26f)));
                    boxes.Add((new Vector3(-0.025f, -0.2f, 0.0f), new Vector3(0.025f, -0.07f, 0.08f), dark));
                    break;
                case "mp5": // MP: kurz, mit gebogenem Magazin nach unten
                    boxes.Add((new Vector3(-0.035f, -0.05f, -0.15f), new Vector3(0.035f, 0.04f, 0.2f), dark));
                    boxes.Add((new Vector3(-0.018f, -0.015f, 0.2f), new Vector3(0.018f, 0.018f, 0.42f), dark));
                    boxes.Add((new Vector3(-0.022f, -0.2f, 0.08f), new Vector3(0.022f, -0.05f, 0.13f), metal));
                    boxes.Add((new Vector3(-0.025f, -0.06f, -0.28f), new Vector3(0.025f, 0.0f, -0.15f), metal));
                    break;
                case "sniper": // Scharfschützengewehr: langer Lauf, Zielfernrohr, Holzschaft
                    boxes.Add((new Vector3(-0.035f, -0.05f, -0.2f), new Vector3(0.035f, 0.035f, 0.3f), wood));
                    boxes.Add((new Vector3(-0.016f, -0.012f, 0.3f), new Vector3(0.016f, 0.018f, 0.8f), dark));
                    boxes.Add((new Vector3(-0.03f, 0.05f, -0.08f), new Vector3(0.03f, 0.1f, 0.22f), dark));
                    boxes.Add((new Vector3(-0.03f, -0.08f, -0.42f), new Vector3(0.03f, 0.02f, -0.2f), wood));
                    break;
                case "pistol": // Pistole: klein, mit Griff
                    boxes.Add((new Vector3(-0.025f, -0.02f, 0.05f), new Vector3(0.025f, 0.03f, 0.3f), dark));
                    boxes.Add((new Vector3(-0.022f, -0.14f, 0.05f), new Vector3(0.022f, -0.02f, 0.12f), metal));
                    break;
                default: // Sturmgewehr (ar15) und alles ohne eigene Form
                    boxes.Add((new Vector3(-0.035f, -0.05f, -0.2f), new Vector3(0.035f, 0.04f, 0.25f), dark));
                    boxes.Add((new Vector3(-0.02f, -0.015f, 0.25f), new Vector3(0.02f, 0.02f, 0.62f), dark));
                    boxes.Add((new Vector3(-0.025f, -0.18f, 0.05f), new Vector3(0.025f, -0.05f, 0.12f), metal));
                    boxes.Add((new Vector3(-0.03f, -0.07f, -0.35f), new Vector3(0.03f, 0.02f, -0.2f), wood));
                    break;
            }
            var root = new GameObject("Waffe " + model);
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
