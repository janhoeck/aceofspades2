using System.Collections.Generic;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Zeigt die anderen Spieler im Netzspiel mit dem Spielermodell (<see cref="PlayerModel"/>). Position, Blick, Animation und
    /// Waffe kommen interpoliert aus der <see cref="ClientSession"/> (leicht verzögert, dafür flüssig); die Pose rechnet
    /// <see cref="PlayerPose"/> aus AoS2.Shared zum selben Zeitpunkt, zu dem der Server beim Lag-Ausgleich prüft.
    /// Tote Spieler werden ausgeblendet.
    /// </summary>
    public sealed class RemotePlayersView : MonoBehaviour
    {
        private const float FlashSeconds = 0.15f;

        private PlayerRig _rig;
        private PlayerPose _pose;
        private Material _material;
        private Material _weaponMaterial;
        private readonly Dictionary<byte, Avatar> _avatars = new Dictionary<byte, Avatar>();
        private readonly List<byte> _gone = new List<byte>();

        private sealed class Avatar
        {
            public PlayerModel Model;
            public float FlashUntil;
        }

        /// <summary>Für Tests und das HUD: Spieler-ID → Wurzel-Objekt (Fußpunkt) der sichtbaren Spieler.</summary>
        public IEnumerable<KeyValuePair<byte, Transform>> Avatars
        {
            get
            {
                foreach (var a in _avatars)
                    if (a.Value.Model.gameObject.activeSelf) yield return new KeyValuePair<byte, Transform>(a.Key, a.Value.Model.transform);
            }
        }

        public int Count => _avatars.Count;

        public void Initialize(PlayerRig rig, Material material, Material weaponMaterial)
        {
            _rig = rig;
            _pose = new PlayerPose(rig);
            _material = material;
            _weaponMaterial = weaponMaterial;
        }

        /// <summary>Modell eines Spielers (oder <c>null</c>), z. B. für Mündungsfeuer an seiner Waffe.</summary>
        public PlayerModel Model(byte id) => _avatars.TryGetValue(id, out var a) ? a.Model : null;

        /// <summary>Getroffen: Modell kurz aufleuchten lassen.</summary>
        public void Flash(byte id)
        {
            if (_avatars.TryGetValue(id, out var a)) a.FlashUntil = Time.time + FlashSeconds;
        }

        /// <summary>Einmal pro Bild: Avatare anlegen, posieren und entfernen, wie die Session es meldet.</summary>
        public void Sync(ClientSession session)
        {
            foreach (var remote in session.RemotePlayers.Values)
            {
                if (!remote.HasState) continue;
                if (!_avatars.TryGetValue(remote.Id, out var avatar))
                {
                    avatar = new Avatar { Model = PlayerModel.Create(_rig, _material, _weaponMaterial, transform, "Spieler " + remote.Name) };
                    _avatars.Add(remote.Id, avatar);
                }
                var s = remote.Current;
                avatar.Model.gameObject.SetActive(s.Alive);
                if (!s.Alive) continue;
                _pose.Evaluate(s.Animation, session.RenderTick, s.Pitch, s.Weapon);
                avatar.Model.transform.position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                avatar.Model.Apply(_pose, s.Position, s.Yaw, s.Weapon);
                avatar.Model.Flash(Mathf.Clamp01((avatar.FlashUntil - Time.time) / FlashSeconds));
            }

            _gone.Clear();
            foreach (var id in _avatars.Keys)
                if (!session.RemotePlayers.ContainsKey(id)) _gone.Add(id);
            foreach (var id in _gone)
            {
                Destroy(_avatars[id].Model.gameObject);
                _avatars.Remove(id);
            }
        }
    }
}
