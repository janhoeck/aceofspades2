using AoS2.Client.Account;
using AoS2.Shared;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Zeigt unten rechts Spiel- und Protokollversion (welche Version hat der Launcher installiert?)
    /// und den angemeldeten Spieler. Erzeugt sich beim Start selbst, braucht also nichts in der Szene.
    /// </summary>
    public sealed class VersionDisplay : MonoBehaviour
    {
        private GUIStyle _textStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(VersionDisplay));
            go.AddComponent<VersionDisplay>();
            DontDestroyOnLoad(go);
        }

        private void OnGUI()
        {
            _textStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerRight };

            var text = $"ACE OF SPADES 2  ·  Version {Application.version}  ·  Protokoll {ProtocolInfo.Version}\n{AccountText()}";
            var rect = new Rect(0, 0, Screen.width - 12, Screen.height - 8);
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _textStyle);
            GUI.color = old;
            GUI.Label(rect, text, _textStyle);
        }

        private static string AccountText()
        {
            switch (PlayerAccount.State)
            {
                case AccountState.SignedIn: return $"Angemeldet als {PlayerAccount.Account.DisplayName}";
                case AccountState.NotSignedIn: return "Nicht angemeldet – bitte über den Launcher starten";
                case AccountState.Expired: return "Anmeldung abgelaufen – bitte im Launcher neu anmelden";
                case AccountState.Unreachable: return "Anmeldeserver nicht erreichbar";
                default: return "Prüfe Anmeldung …";
            }
        }
    }
}
