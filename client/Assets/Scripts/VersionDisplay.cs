using AoS2.Client.Account;
using AoS2.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AoS2.Client
{
    /// <summary>
    /// Platzhalter: zeigt Spiel- und Protokollversion (welche Version hat der Launcher installiert?)
    /// und den angemeldeten Spieler. Erzeugt sich beim Start selbst, braucht also nichts in der Szene.
    /// </summary>
    public sealed class VersionDisplay : MonoBehaviour
    {
        private GUIStyle _titleStyle;
        private GUIStyle _textStyle;
        private Texture2D _panel;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(VersionDisplay));
            go.AddComponent<VersionDisplay>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                Application.Quit();
        }

        private void OnGUI()
        {
            _titleStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _textStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.11f, 0.85f));
                _panel.Apply();
            }

            // Dunkler Streifen hinter dem Text, sonst ist Weiß auf dem hellen Himmel kaum lesbar.
            GUI.DrawTexture(new Rect(0, Screen.height / 2f - 110, Screen.width, 250), _panel);

            var center = new Rect(0, Screen.height / 2f - 90, Screen.width, 60);
            GUI.Label(center, "ACE OF SPADES 2", _titleStyle);

            center.y += 70;
            GUI.Label(center, $"Version {Application.version}   ·   Protokoll {ProtocolInfo.Version}", _textStyle);

            center.y += 40;
            GUI.Label(center, AccountText(), _textStyle);

            center.y += 40;
            GUI.Label(center, "Platzhalter – ESC beendet das Spiel", _textStyle);
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
