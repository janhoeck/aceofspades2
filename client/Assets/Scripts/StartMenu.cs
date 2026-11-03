using AoS2.Client.Account;
using AoS2.Shared;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Platzhalter-Startmenü (bis M7 der Server-Browser kommt): Name, Server-Adresse, Verbinden oder offline spielen.
    /// Erzeugt sich beim Start selbst; die Eingaben merkt es sich in den PlayerPrefs.
    /// </summary>
    public sealed class StartMenu : MonoBehaviour
    {
        private const string PrefName = "aos2.menu.name";
        private const string PrefAddress = "aos2.menu.address";
        private const string PrefDevTicket = "aos2.menu.devTicket";

        private string _name;
        private string _address;
        private bool _devTicket;
        private bool _devTicketChosen;
        private GUIStyle _title;
        private GUIStyle _label;
        private GUIStyle _note;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(StartMenu));
            go.AddComponent<StartMenu>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            _name = PlayerPrefs.GetString(PrefName, "Spieler");
            _address = PlayerPrefs.GetString(PrefAddress, "127.0.0.1:" + ProtocolInfo.DefaultPort);
            _devTicketChosen = PlayerPrefs.HasKey(PrefDevTicket);
            _devTicket = PlayerPrefs.GetInt(PrefDevTicket, 1) == 1;
        }

        private void OnGUI()
        {
            var game = GameClient.Instance;
            if (game == null || game.LoadError != null || game.IsReady) return;
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 16 };
            _note ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };

            const float width = 480, height = 380;
            var area = new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 20, area.y + 16, width - 40, height - 32));
            GUILayout.Label("Ace of Spades 2", _title);
            GUILayout.Space(10);

            var busy = game.Mode != GameMode.Menu;
            GUI.enabled = !busy;

            // Ohne Anmeldung geht nur ein Entwicklungs-Ticket; mit Anmeldung ist das echte Ticket die Vorgabe.
            if (!_devTicketChosen && PlayerAccount.State != AccountState.Checking) _devTicket = !PlayerAccount.IsSignedIn;

            if (PlayerAccount.IsSignedIn && !_devTicket)
            {
                GUILayout.Label("Angemeldet als " + PlayerAccount.Account.DisplayName, _label);
            }
            else
            {
                GUILayout.Label("Name", _label);
                _name = GUILayout.TextField(_name ?? "", 24);
            }
            GUILayout.Label("Server", _label);
            _address = GUILayout.TextField(_address ?? "", 100);
            var dev = GUILayout.Toggle(_devTicket, " Entwicklungs-Ticket verwenden (nur für Server mit --dev)");
            if (dev != _devTicket)
            {
                _devTicket = dev;
                _devTicketChosen = true;
            }
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Verbinden", GUILayout.Height(34)))
            {
                Save();
                game.Connect(_address, _name, _devTicket);
            }
            if (GUILayout.Button("Offline spielen", GUILayout.Height(34)))
            {
                Save();
                game.StartOffline();
            }
            GUI.enabled = true;
            if (busy && GUILayout.Button("Abbrechen", GUILayout.Height(34))) game.LeaveToMenu();
            if (!busy && GUILayout.Button("Beenden", GUILayout.Height(34))) Application.Quit();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            if (!string.IsNullOrEmpty(game.Status)) GUILayout.Label(game.Status, _note);
            else if (!PlayerAccount.IsSignedIn)
                GUILayout.Label("Nicht über den Launcher angemeldet: Verbinden geht nur mit einem Server im Entwicklungsmodus (--dev).", _note);
            GUILayout.EndArea();
        }

        private void Save()
        {
            PlayerPrefs.SetString(PrefName, _name ?? "");
            PlayerPrefs.SetString(PrefAddress, _address ?? "");
            if (_devTicketChosen) PlayerPrefs.SetInt(PrefDevTicket, _devTicket ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
