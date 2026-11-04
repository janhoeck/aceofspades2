using System.Collections.Generic;
using AoS2.Shared.Maps;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Rig;
using UnityEngine;

namespace AoS2.Client.Player
{
    /// <summary>
    /// Zeigt die anderen Spieler im Netzspiel mit dem Spielermodell (<see cref="PlayerModel"/>). Position, Blick, Animation,
    /// Klasse und Waffe kommen interpoliert aus der <see cref="ClientSession"/> (leicht verzögert, dafür flüssig); die Pose
    /// rechnet <see cref="PlayerPose"/> aus AoS2.Shared zum selben Zeitpunkt, zu dem der Server beim Lag-Ausgleich prüft.
    /// Die Textur zeigt das Team (Blau oder Grün), die Ausrüstung die Klasse. Wechselt die Klasse (beim Respawn), wird das
    /// Modell mit der neuen Ausrüstung neu gebaut. Tote Spieler und Zuschauer werden ausgeblendet.
    /// </summary>
    public sealed class RemotePlayersView : MonoBehaviour
    {
        private const float FlashSeconds = 0.15f;

        private PlayerRig _rig;
        private PlayerPose _pose;
        private Material _blue;
        private Material _green;
        private Material _weaponMaterial;
        private readonly Dictionary<byte, Avatar> _avatars = new Dictionary<byte, Avatar>();
        private readonly List<byte> _gone = new List<byte>();

        private sealed class Avatar
        {
            public PlayerModel Model;
            public Team Team;
            public byte Class;
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

        public void Initialize(PlayerRig rig, float minHitboxPixels, Material blue, Material green, Material weaponMaterial)
        {
            _rig = rig;
            _pose = new PlayerPose(rig, minHitboxPixels);
            _blue = blue;
            _green = green;
            _weaponMaterial = weaponMaterial;
        }

        /// <summary>Modell eines Spielers (oder <c>null</c>), z. B. für Mündungsfeuer an seiner Waffe.</summary>
        public PlayerModel Model(byte id) => _avatars.TryGetValue(id, out var a) ? a.Model : null;

        /// <summary>Team, dessen Farbe das Modell gerade trägt (für Tests).</summary>
        public Team ShownTeam(byte id) => _avatars.TryGetValue(id, out var a) ? a.Team : Team.None;

        /// <summary>Ausrüstung, die das Modell gerade trägt (für Tests), <c>null</c> = keine.</summary>
        public string ShownGear(byte id) => _avatars.TryGetValue(id, out var a) ? a.Model.GearClass : null;

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
                var s = remote.Current;
                var cls = session.Classes.Get(s.Class) ?? session.Classes.None;
                if (_avatars.TryGetValue(remote.Id, out var avatar) && avatar.Class != s.Class)
                {
                    Destroy(avatar.Model.gameObject); // neue Klasse: anderes Modell (Ausrüstung)
                    _avatars.Remove(remote.Id);
                    avatar = null;
                }
                if (avatar == null)
                {
                    var model = PlayerModel.Create(_rig, MaterialFor(remote.Team), _weaponMaterial, transform, $"Spieler {remote.Name} ({cls.DisplayName})", cls.Gear);
                    avatar = new Avatar { Model = model, Team = remote.Team, Class = s.Class };
                    _avatars.Add(remote.Id, avatar);
                }
                if (avatar.Team != remote.Team)
                {
                    avatar.Team = remote.Team;
                    avatar.Model.SetMaterial(MaterialFor(remote.Team));
                }
                var visible = s.Alive && remote.Team != Team.None;
                avatar.Model.gameObject.SetActive(visible);
                if (!visible) continue;
                _pose.Evaluate(s.Animation, session.RenderTick, s.Pitch, s.Weapon);
                avatar.Model.transform.position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                avatar.Model.Apply(_pose, s.Position, s.Yaw, cls.Weapon(s.Weapon)?.Model ?? WeaponView.SpadeModel);
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

        private Material MaterialFor(Team team) => team == Team.Green ? _green : _blue;
    }
}
