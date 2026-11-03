using System.Collections.Generic;
using AoS2.Shared;
using AoS2.Shared.Rig;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Das sichtbare Spielermodell, gebaut aus <c>data/player_rig.json</c>: je Knochen ein Mesh aus seinen Würfeln (mit den
    /// Texturkoordinaten aus Blockbench). Die Knochen setzt <see cref="Apply"/> aus der <see cref="PlayerPose"/> von
    /// AoS2.Shared, also mit denselben Zahlen, mit denen der Server die Trefferboxen prüft. Einen Unity-Animator gibt es
    /// für den Körper bewusst nicht, sonst könnten Bild und Trefferzonen auseinanderlaufen.
    /// <para>
    /// Blockbench rechnet rechtshändig mit Blick nach −z, Unity linkshändig mit Blick nach +z. Der Übergang ist eine
    /// Spiegelung von z (<see cref="PlayerPose.ModelToWorld"/>): Die Meshes liegen schon gespiegelt vor, die Drehungen der
    /// Knochen werden mitgespiegelt (F · R · F), so bleibt jede Drehung eine echte Drehung für Unitys Transform.
    /// </para>
    /// </summary>
    public sealed class PlayerModel : MonoBehaviour
    {
        private Transform[] _bones;
        private Transform _weapon;
        private Renderer[] _renderers;
        private int _weaponSlot = -1;
        private MaterialPropertyBlock _flash;

        /// <summary>Knochen der rechten Hand (für Waffe und Mündungsfeuer).</summary>
        public Transform RightHand { get; private set; }

        /// <summary>Mündung der Waffe in der Hand (Weltkoordinaten), nach <see cref="Apply"/>.</summary>
        public Vector3 Muzzle => _weapon != null ? _weapon.TransformPoint(new Vector3(0, -WeaponLength(_weaponSlot), 0)) : transform.position + Vector3.up * 1.4f;

        public static PlayerModel Create(PlayerRig rig, Material material, Material weaponMaterial, Transform parent, string name, string gearClass = null)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            var model = root.AddComponent<PlayerModel>();
            model._bones = new Transform[rig.Bones.Count];
            var renderers = new List<Renderer>();
            var meshes = BuildMeshes(rig, gearClass);
            for (var i = 0; i < rig.Bones.Count; i++)
            {
                var bone = new GameObject(rig.Bones[i].Name).transform;
                bone.SetParent(root.transform, false);
                bone.localScale = Vector3.one / PlayerRig.PixelsPerBlock;
                model._bones[i] = bone;
                if (meshes[i] == null) continue;
                bone.gameObject.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var r = bone.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                renderers.Add(r);
            }

            var hand = rig.BoneIndex("arm_lower_right");
            model.RightHand = hand >= 0 ? model._bones[hand] : root.transform;
            model._weapon = new GameObject("Waffe").transform;
            model._weapon.SetParent(model.RightHand, false);
            model._weapon.gameObject.AddComponent<MeshFilter>();
            var weaponRenderer = model._weapon.gameObject.AddComponent<MeshRenderer>();
            weaponRenderer.sharedMaterial = weaponMaterial;
            var weaponColor = new MaterialPropertyBlock();
            weaponColor.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.22f));
            weaponRenderer.SetPropertyBlock(weaponColor);
            model._renderers = renderers.ToArray();
            model._flash = new MaterialPropertyBlock();
            return model;
        }

        /// <summary>Setzt alle Knochen aus der Pose, am Fußpunkt <paramref name="feet"/> mit Blick <paramref name="yaw"/>.</summary>
        public void Apply(PlayerPose pose, Vec3 feet, float yaw, int weaponSlot)
        {
            var toWorld = PlayerPose.ModelToWorld(feet, yaw);
            var turn = Quaternion.Euler(0, yaw, 0);
            for (var i = 0; i < _bones.Length; i++)
            {
                var m = pose.Bone(i);
                var position = toWorld.TransformPoint(m.Position);
                _bones[i].SetPositionAndRotation(new Vector3(position.X, position.Y, position.Z), turn * MirroredRotation(m));
            }
            if (weaponSlot != _weaponSlot)
            {
                _weaponSlot = weaponSlot;
                _weapon.GetComponent<MeshFilter>().sharedMesh = WeaponMeshes.ForSlot(weaponSlot);
            }
        }

        private void OnDestroy()
        {
            foreach (var bone in _bones)
                if (bone != null && bone.TryGetComponent<MeshFilter>(out var filter)) Destroy(filter.sharedMesh);
        }

        /// <summary>Lässt das Modell kurz aufleuchten (Treffer).</summary>
        public void Flash(float amount)
        {
            _flash.SetFloat("_Emission", amount);
            _flash.SetColor("_BaseColor", Color.Lerp(Color.white, new Color(1f, 0.55f, 0.55f), amount));
            foreach (var r in _renderers) r.SetPropertyBlock(_flash);
        }

        /// <summary>Drehung F · R · F (z gespiegelt) als Quaternion.</summary>
        private static Quaternion MirroredRotation(Mat34 m)
        {
            var forward = new Vector3(-m.M02, -m.M12, m.M22); // dritte Spalte, gespiegelt
            var up = new Vector3(m.M01, m.M11, -m.M21); // zweite Spalte, gespiegelt
            return Quaternion.LookRotation(forward, up);
        }

        private static float WeaponLength(int slot) => slot == 1 ? 22 : slot == 3 ? 16 : 6;

        // ---- Meshes ----

        // Seiten wie in Blockbench (Norden = −z, Osten = +x, …). Je Seite: Normale und die vier Ecken als Bits
        // (x = 4, y = 2, z = 1; 0 = from, 1 = to) in der Reihenfolge links oben, rechts oben, rechts unten, links unten,
        // so wie man von außen auf die Seite schaut. Abgeleitet aus Blockbenchs Box-UV-Netz (Nachbarseiten teilen Kanten).
        private static readonly Vector3[] FaceNormals = { new(0, 0, -1), new(1, 0, 0), new(0, 0, 1), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0) };
        private static readonly int[][] FaceCorners =
        {
            new[] { 0b110, 0b010, 0b000, 0b100 }, // Norden: links = +x
            new[] { 0b111, 0b110, 0b100, 0b101 }, // Osten: links = +z
            new[] { 0b011, 0b111, 0b101, 0b001 }, // Süden: links = −x
            new[] { 0b010, 0b011, 0b001, 0b000 }, // Westen: links = −z
            new[] { 0b010, 0b110, 0b111, 0b011 }, // oben: links = −x, oben = Norden
            new[] { 0b000, 0b100, 0b101, 0b001 }, // unten: wie oben
        };

        /// <summary>Ein Mesh je Knochen (oder <c>null</c>, wenn er keine Würfel hat), Ecken relativ zum Drehpunkt, z gespiegelt.</summary>
        private static Mesh[] BuildMeshes(PlayerRig rig, string gearClass)
        {
            var meshes = new Mesh[rig.Bones.Count];
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (var bone = 0; bone < rig.Bones.Count; bone++)
            {
                vertices.Clear();
                normals.Clear();
                uvs.Clear();
                triangles.Clear();
                var pivot = rig.Bones[bone].Pivot;
                foreach (var box in rig.Boxes)
                {
                    if (box.Bone != bone || (box.Gear != null && box.Gear != gearClass)) continue;
                    for (var f = 0; f < 6; f++)
                    {
                        var uv = box.FaceUvs[f];
                        if (uv == null) continue;
                        var start = vertices.Count;
                        var c = FaceCorners[f];
                        for (var k = 0; k < 4; k++)
                        {
                            var p = new Vec3((c[k] & 0b100) != 0 ? box.To.X : box.From.X, (c[k] & 0b010) != 0 ? box.To.Y : box.From.Y,
                                (c[k] & 0b001) != 0 ? box.To.Z : box.From.Z) - pivot;
                            vertices.Add(new Vector3(p.X, p.Y, -p.Z));
                            normals.Add(new Vector3(FaceNormals[f].x, FaceNormals[f].y, -FaceNormals[f].z));
                        }
                        // u0/v0 = links oben, u1/v1 = rechts unten (Pixel, v nach unten → Unity v nach oben)
                        float w = rig.TextureWidth, h = rig.TextureHeight;
                        uvs.Add(new Vector2(uv[0] / w, 1 - uv[1] / h));
                        uvs.Add(new Vector2(uv[2] / w, 1 - uv[1] / h));
                        uvs.Add(new Vector2(uv[2] / w, 1 - uv[3] / h));
                        uvs.Add(new Vector2(uv[0] / w, 1 - uv[3] / h));
                        AddQuad(triangles, vertices, normals[start], start);
                    }
                }
                if (vertices.Count == 0) continue;
                var mesh = new Mesh { name = rig.Bones[bone].Name };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                meshes[bone] = mesh;
            }
            return meshes;
        }

        /// <summary>Zwei Dreiecke, so gedreht, dass die Vorderseite nach außen zeigt (Unity: im Uhrzeigersinn von vorn).</summary>
        internal static void AddQuad(List<int> triangles, List<Vector3> vertices, Vector3 normal, int start)
        {
            var a = vertices[start];
            var cross = Vector3.Cross(vertices[start + 1] - a, vertices[start + 2] - a);
            if (Vector3.Dot(cross, normal) >= 0) triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            else triangles.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
        }
    }

    /// <summary>Platzhalter-Waffen aus Quadern (Pixel, im Knochen der rechten Hand: Arm zeigt nach −y).</summary>
    public static class WeaponMeshes
    {
        private static readonly Dictionary<int, Mesh> Cache = new Dictionary<int, Mesh>();

        public static Mesh ForSlot(int slot)
        {
            if (Cache.TryGetValue(slot, out var mesh) && mesh != null) return mesh;
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            switch (slot)
            {
                case 0: // Spaten: Stiel und Blatt
                    boxes.Add((new Vector3(-0.5f, -16, -0.5f), new Vector3(0.5f, -3, 0.5f)));
                    boxes.Add((new Vector3(-2, -21, -0.4f), new Vector3(2, -16, 0.4f)));
                    break;
                case 1: // Gewehr: Lauf, Gehäuse, Magazin
                    boxes.Add((new Vector3(-0.6f, -22, -0.6f), new Vector3(0.6f, -10, 0.6f)));
                    boxes.Add((new Vector3(-1, -12, -1.6f), new Vector3(1, -2, 1.2f)));
                    boxes.Add((new Vector3(-0.7f, -9, 1.2f), new Vector3(0.7f, -6, 4)));
                    break;
                case 2: // Granate in der Hand
                    boxes.Add((new Vector3(-1.5f, -7.5f, -1.5f), new Vector3(1.5f, -4, 1.5f)));
                    break;
                default: // Raketenwerfer: dickes Rohr über die Schulter
                    boxes.Add((new Vector3(-1.8f, -16, -1.8f), new Vector3(1.8f, 6, 1.8f)));
                    boxes.Add((new Vector3(-0.6f, -8, 1.8f), new Vector3(0.6f, -5, 4)));
                    break;
            }
            mesh = BoxMesh(boxes, "Waffe " + slot);
            Cache[slot] = mesh;
            return mesh;
        }

        /// <summary>Mesh aus achsenparallelen Quadern (UV überall 0 … 1).</summary>
        public static Mesh BoxMesh(IReadOnlyList<(Vector3 Min, Vector3 Max)> boxes, string name)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var dirs = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var (min, max) in boxes)
            {
                var center = (min + max) / 2;
                var half = (max - min) / 2;
                foreach (var n in dirs)
                {
                    // Zwei Achsen der Fläche
                    var u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                    var v = Vector3.Cross(n, u);
                    var start = vertices.Count;
                    var faceCenter = center + Vector3.Scale(n, half);
                    var su = Vector3.Scale(u, half);
                    var sv = Vector3.Scale(v, half);
                    vertices.Add(faceCenter - su - sv);
                    vertices.Add(faceCenter + su - sv);
                    vertices.Add(faceCenter + su + sv);
                    vertices.Add(faceCenter - su + sv);
                    for (var k = 0; k < 4; k++) normals.Add(n);
                    uvs.Add(new Vector2(0, 0));
                    uvs.Add(new Vector2(1, 0));
                    uvs.Add(new Vector2(1, 1));
                    uvs.Add(new Vector2(0, 1));
                    PlayerModel.AddQuad(triangles, vertices, n, start);
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
