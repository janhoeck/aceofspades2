using System.Collections.Generic;
using AoS2.Client.World;
using AoS2.Shared.Movement;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Zeigt die anderen Spieler im Netzspiel als Platzhalter: ein Körper aus zwei Baublöcken in Größe des
    /// Kollisionsquaders und ein kleiner Block als „Nase“ in Blickrichtung. Positionen kommen interpoliert aus der
    /// <see cref="ClientSession"/> (leicht verzögert, dafür flüssig). Das richtige Spielermodell kommt später.
    /// </summary>
    public sealed class RemotePlayersView : MonoBehaviour
    {
        private const float NoseSize = 0.3f;

        private MovementSettings _movement;
        private Material _material;
        private Mesh _body;
        private Mesh _nose;
        private readonly Dictionary<byte, Avatar> _avatars = new Dictionary<byte, Avatar>();
        private readonly List<byte> _gone = new List<byte>();

        private sealed class Avatar
        {
            public GameObject Root;
            public Transform Body;
            public Transform Head;
        }

        /// <summary>Für Tests und das HUD: Spieler-ID → Wurzel-Objekt (Fußpunkt).</summary>
        public IEnumerable<KeyValuePair<byte, Transform>> Avatars
        {
            get
            {
                foreach (var a in _avatars) yield return new KeyValuePair<byte, Transform>(a.Key, a.Value.Root.transform);
            }
        }

        public int Count => _avatars.Count;

        public void Initialize(BlockRegistry registry, MovementSettings movement, VoxelMeshBuilder builder, Material material)
        {
            _movement = movement;
            _material = material;
            byte bodyBlock = 0, noseBlock = 0;
            foreach (var t in registry.All)
            {
                if (bodyBlock == 0 && t.Buildable) bodyBlock = t.Id;
                if (noseBlock == 0 && !t.Buildable && !t.Indestructible) noseBlock = t.Id;
            }
            if (bodyBlock == 0) bodyBlock = registry.All[0].Id;
            if (noseBlock == 0) noseBlock = bodyBlock;
            _body = BuildColumn(builder, bodyBlock, 2, "Spieler-Körper");
            _nose = BuildColumn(builder, noseBlock, 1, "Spieler-Nase");
        }

        /// <summary>Einmal pro Bild: Avatare anlegen, bewegen und entfernen, wie die Session es meldet.</summary>
        public void Sync(ClientSession session)
        {
            foreach (var remote in session.RemotePlayers.Values)
            {
                if (!remote.HasState) continue;
                if (!_avatars.TryGetValue(remote.Id, out var avatar))
                {
                    avatar = Create(remote.Name);
                    _avatars.Add(remote.Id, avatar);
                }
                var s = remote.Current;
                var height = _movement.Height(s.Crouching);
                avatar.Root.transform.SetPositionAndRotation(new Vector3(s.Position.X, s.Position.Y, s.Position.Z), Quaternion.Euler(0, s.Yaw, 0));
                avatar.Body.localScale = new Vector3(2 * _movement.HalfWidth, height / 2, 2 * _movement.HalfWidth);
                avatar.Head.localPosition = new Vector3(0, _movement.EyeHeight(s.Crouching), 0);
                avatar.Head.localRotation = Quaternion.Euler(s.Pitch, 0, 0);
            }

            _gone.Clear();
            foreach (var id in _avatars.Keys)
                if (!session.RemotePlayers.ContainsKey(id)) _gone.Add(id);
            foreach (var id in _gone)
            {
                Destroy(_avatars[id].Root);
                _avatars.Remove(id);
            }
        }

        private Avatar Create(string name)
        {
            var root = new GameObject("Spieler " + name);
            root.transform.SetParent(transform, false);

            var body = MeshObject("Körper", _body, root.transform);
            body.transform.localPosition = new Vector3(-_movement.HalfWidth, 0, -_movement.HalfWidth);

            var head = new GameObject("Kopf").transform;
            head.SetParent(root.transform, false);
            var nose = MeshObject("Nase", _nose, head);
            nose.transform.localScale = Vector3.one * NoseSize;
            nose.transform.localPosition = new Vector3(-NoseSize / 2, -NoseSize / 2, _movement.HalfWidth);

            return new Avatar { Root = root, Body = body.transform, Head = head };
        }

        private GameObject MeshObject(string name, Mesh mesh, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            return go;
        }

        /// <summary>Mesh einer Säule aus <paramref name="height"/> Blöcken (Ursprung unten links vorne), mit dem Mesh-Job der Welt.</summary>
        private static Mesh BuildColumn(VoxelMeshBuilder builder, byte id, int height, string name)
        {
            var size = new int3(1, height, 1);
            var padded = size + 2;
            var blocks = new NativeArray<byte>(padded.x * padded.y * padded.z, Allocator.TempJob);
            for (var y = 0; y < height; y++) blocks[((y + 1) * padded.z + 1) * padded.x + 1] = id;
            var mesh = new Mesh { name = name };
            builder.Schedule(blocks, size, mesh);
            builder.CompleteAll();
            return mesh;
        }

        private void OnDestroy()
        {
            if (_body != null) Destroy(_body);
            if (_nose != null) Destroy(_nose);
        }
    }
}
