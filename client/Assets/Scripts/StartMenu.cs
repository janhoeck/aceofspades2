using AoS2.Client.Account;
using AoS2.Net;
using AoS2.Shared;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Startmenü (Platzhalter-Design bis M9) mit zwei Reitern:
    /// <list type="bullet">
    /// <item><b>Server-Browser</b>: Liste vom Backend mit Ping, Filtern und Sortierung (<see cref="ServerBrowser"/>); Beitreten per
    ///       Doppelklick oder Knopf, mit Join-Ticket vom Backend.</item>
    /// <item><b>Direkt verbinden</b>: Adresse <c>host:port</c> eintippen, optional Entwicklungs-Ticket (Server mit <c>--dev</c>).</item>
    /// </list>
    /// Dazu „Offline spielen“. Erzeugt sich beim Start selbst; die Eingaben merkt es sich in den PlayerPrefs.
    /// </summary>
    public sealed class StartMenu : MonoBehaviour
    {
        private const string PrefName = "aos2.menu.name";
        private const string PrefAddress = "aos2.menu.address";
        private const string PrefDevTicket = "aos2.menu.devTicket";
        private const string PrefTab = "aos2.menu.tab";

        private const int TabBrowser = 0;
        private const int TabDirect = 1;
        private static readonly string[] Tabs = { "Server-Browser", "Direkt verbinden" };
        private static readonly string[] SortNames = { "Spieler", "Ping", "Name" };

        private string _name;
        private string _address;
        private bool _devTicket;
        private bool _devTicketChosen;
        private int _tab;
        private const float DoubleClickSeconds = 0.4f;

        private string _selected;
        private float _lastClick = -1f;
        private Vector2 _scroll;
        private GUIStyle _title;
        private GUIStyle _label;
        private GUIStyle _note;
        private GUIStyle _header;
        private GUIStyle _row;
        private GUIStyle _rowSelected;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            var go = new GameObject(nameof(StartMenu));
            go.AddComponent<StartMenu>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Gewählter Reiter (für Tests).</summary>
        public static int Tab { get; set; } = -1;

        private void Awake()
        {
            _name = PlayerPrefs.GetString(PrefName, "Spieler");
            _address = PlayerPrefs.GetString(PrefAddress, "127.0.0.1:" + ProtocolInfo.DefaultPort);
            _devTicketChosen = PlayerPrefs.HasKey(PrefDevTicket);
            _devTicket = PlayerPrefs.GetInt(PrefDevTicket, 1) == 1;
            _tab = Mathf.Clamp(PlayerPrefs.GetInt(PrefTab, TabBrowser), TabBrowser, TabDirect);
        }

        private bool MenuVisible
        {
            get
            {
                var game = GameClient.Instance;
                return game != null && game.LoadError == null && !game.IsReady;
            }
        }

        private void Update()
        {
            if (Tab >= 0) _tab = Tab;
            var browser = ServerBrowser.Instance;
            if (browser != null) browser.Active = MenuVisible && _tab == TabBrowser;
        }

        private void OnGUI()
        {
            if (!MenuVisible) return;
            var game = GameClient.Instance;
            CreateStyles();

            var width = Mathf.Min(860f, Screen.width - 40f);
            var height = Mathf.Min(600f, Screen.height - 40f);
            var area = new Rect((Screen.width - width) / 2, (Screen.height - height) / 2, width, height);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 20, area.y + 14, width - 40, height - 28));
            GUILayout.Label("Ace of Spades 2", _title);
            GUILayout.Space(6);

            var busy = game.Mode != GameMode.Menu;
            GUI.enabled = !busy;

            // Ohne Anmeldung geht nur ein Entwicklungs-Ticket; mit Anmeldung ist das echte Ticket die Vorgabe.
            if (!_devTicketChosen && PlayerAccount.State != AccountState.Checking) _devTicket = !PlayerAccount.IsSignedIn;

            GUILayout.BeginHorizontal();
            var tab = GUILayout.Toolbar(_tab, Tabs, GUILayout.Height(28), GUILayout.Width(360));
            if (tab != _tab)
            {
                _tab = tab;
                Tab = -1;
                PlayerPrefs.SetInt(PrefTab, _tab);
                if (_tab == TabBrowser) ServerBrowser.Instance?.Refresh();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(PlayerAccount.IsSignedIn ? "Angemeldet als " + PlayerAccount.Account.DisplayName : AccountText(), _label);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            if (_tab == TabBrowser) DrawBrowser(game);
            else DrawDirect(game);

            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(game.Status)) GUILayout.Label(game.Status, _note);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Offline spielen", GUILayout.Height(32), GUILayout.Width(160)))
            {
                Save();
                game.StartOffline();
            }
            GUILayout.FlexibleSpace();
            GUI.enabled = true;
            if (busy && GUILayout.Button("Abbrechen", GUILayout.Height(32), GUILayout.Width(120))) game.LeaveToMenu();
            if (!busy && GUILayout.Button("Beenden", GUILayout.Height(32), GUILayout.Width(120))) Application.Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private static string AccountText()
        {
            switch (PlayerAccount.State)
            {
                case AccountState.Checking: return "Prüfe Anmeldung …";
                case AccountState.Expired: return "Anmeldung abgelaufen (Launcher)";
                case AccountState.Unreachable: return "Anmeldeserver nicht erreichbar";
                default: return "Nicht angemeldet";
            }
        }

        // ---- Server-Browser ----

        private void DrawBrowser(GameClient game)
        {
            var browser = ServerBrowser.Instance;
            if (browser == null) return;
            var filter = browser.Filter;

            GUILayout.BeginHorizontal();
            var hideFull = GUILayout.Toggle(filter.HideFull, " Volle ausblenden", GUILayout.Width(150));
            var hideEmpty = GUILayout.Toggle(filter.HideEmpty, " Leere ausblenden", GUILayout.Width(150));
            GUILayout.Label("Map:", GUILayout.Width(36));
            if (GUILayout.Button(filter.Map ?? "alle", GUILayout.Width(130))) filter.Map = NextMap(browser, filter.Map);
            GUILayout.Label("Sortieren:", GUILayout.Width(70));
            var sort = (ServerSort)GUILayout.Toolbar((int)filter.Sort, SortNames, GUILayout.Width(210));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(browser.IsLoading ? "Lädt …" : "Aktualisieren", GUILayout.Width(110))) browser.Refresh();
            GUILayout.EndHorizontal();
            if (hideFull != filter.HideFull || hideEmpty != filter.HideEmpty || sort != filter.Sort)
            {
                filter.HideFull = hideFull;
                filter.HideEmpty = hideEmpty;
                filter.Sort = sort;
                browser.SaveFilter();
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _header, GUILayout.ExpandWidth(true));
            GUILayout.Label("Map", _header, GUILayout.Width(140));
            GUILayout.Label("Spieler", _header, GUILayout.Width(70));
            GUILayout.Label("Ping", _header, GUILayout.Width(70));
            GUILayout.EndHorizontal();

            var rows = browser.Rows;
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            foreach (var row in rows)
            {
                var entry = row.Entry;
                var selected = string.Equals(entry.Address, _selected, System.StringComparison.OrdinalIgnoreCase);
                var rect = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
                if (GUI.Button(rect, GUIContent.none, selected ? _rowSelected : _row))
                {
                    // Doppelklick: zweiter Klick auf dieselbe Zeile kurz nach dem ersten.
                    var now = Time.realtimeSinceStartup;
                    if (selected && now - _lastClick < DoubleClickSeconds) Join(game, entry.Address);
                    _selected = entry.Address;
                    _lastClick = now;
                }
                var x = rect.x + 6;
                var nameWidth = rect.width - 140 - 70 - 70 - 12;
                GUI.Label(new Rect(x, rect.y + 3, nameWidth, 22), (entry.Official ? "★ " : "") + entry.Name, _label);
                GUI.Label(new Rect(rect.xMax - 280, rect.y + 3, 140, 22), entry.Map, _label);
                GUI.Label(new Rect(rect.xMax - 140, rect.y + 3, 70, 22), $"{row.Players}/{row.MaxPlayers}", _label);
                GUI.Label(new Rect(rect.xMax - 70, rect.y + 3, 70, 22), PingText(row.Ping), _label);
            }
            GUILayout.EndScrollView();

            GUILayout.Label(BrowserStatus(browser, rows.Count), _note);
            GUILayout.BeginHorizontal();
            GUI.enabled = GUI.enabled && _selected != null && Contains(rows, _selected);
            if (GUILayout.Button("Beitreten", GUILayout.Height(32), GUILayout.Width(160))) Join(game, _selected);
            GUI.enabled = game.Mode == GameMode.Menu;
            GUILayout.Label(_selected != null ? "  " + _selected : "", _note);
            GUILayout.EndHorizontal();
        }

        private void Join(GameClient game, string address)
        {
            if (game.Mode != GameMode.Menu || address == null) return;
            Save();
            // Server im Browser laufen nie im Entwicklungsmodus: immer mit echtem Join-Ticket.
            game.Connect(address, _name, false);
        }

        private static bool Contains(System.Collections.Generic.IReadOnlyList<ServerRow> rows, string address)
        {
            foreach (var row in rows)
                if (string.Equals(row.Entry.Address, address, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NextMap(ServerBrowser browser, string current)
        {
            var maps = browser.Maps;
            if (maps.Count == 0) return null;
            if (current == null) return maps[0];
            for (var i = 0; i < maps.Count; i++)
                if (string.Equals(maps[i], current, System.StringComparison.OrdinalIgnoreCase))
                    return i + 1 < maps.Count ? maps[i + 1] : null;
            return null;
        }

        private static string BrowserStatus(ServerBrowser browser, int visible)
        {
            if (browser.Error != null) return browser.Error;
            if (!browser.HasList) return browser.IsLoading ? "Lade Serverliste …" : "";
            if (browser.Total == 0) return "Gerade ist kein Server online. Über \"Direkt verbinden\" kommt man auf jeden Server, dessen Adresse man kennt.";
            var hidden = browser.Total - visible;
            return $"{visible} Server" + (hidden > 0 ? $" ({hidden} ausgeblendet)" : "") + ". Doppelklick zum Beitreten.";
        }

        private static string PingText(PingResult ping)
        {
            if (ping == null) return "–";
            switch (ping.State)
            {
                case PingState.Answered: return ping.RoundTripMs + " ms";
                case PingState.Pending: return "…";
                default: return "keine Antwort";
            }
        }

        // ---- Direkt verbinden ----

        private void DrawDirect(GameClient game)
        {
            if (PlayerAccount.IsSignedIn && !_devTicket)
            {
                GUILayout.Label("Du trittst als " + PlayerAccount.Account.DisplayName + " bei.", _label);
            }
            else
            {
                GUILayout.Label("Name", _label);
                _name = GUILayout.TextField(_name ?? "", 24, GUILayout.Width(360));
            }
            GUILayout.Label("Server (IP oder Name, mit Port), z. B. play.example.org:" + ProtocolInfo.DefaultPort, _label);
            GUILayout.BeginHorizontal();
            _address = GUILayout.TextField(_address ?? "", 100, GUILayout.Width(360));
            if (GUILayout.Button("Prüfen", GUILayout.Width(80))) ServerBrowser.Instance?.Ping(_address);
            GUILayout.EndHorizontal();
            var ping = ServerBrowser.Instance?.PingOf(_address);
            if (ping != null) GUILayout.Label(DirectPingText(ping), _note);

            var dev = GUILayout.Toggle(_devTicket, " Entwicklungs-Ticket verwenden (nur für Server mit --dev)");
            if (dev != _devTicket)
            {
                _devTicket = dev;
                _devTicketChosen = true;
            }
            GUILayout.Space(8);
            if (GUILayout.Button("Verbinden", GUILayout.Height(32), GUILayout.Width(160)))
            {
                Save();
                game.Connect(_address, _name, _devTicket);
            }
            if (!PlayerAccount.IsSignedIn && string.IsNullOrEmpty(game.Status))
                GUILayout.Label("Nicht über den Launcher angemeldet: Verbinden geht nur mit einem Server im Entwicklungsmodus (--dev).", _note);
        }

        private static string DirectPingText(PingResult ping)
        {
            switch (ping.State)
            {
                case PingState.Pending: return "Frage den Server …";
                case PingState.Unresolvable: return "Diese Adresse gibt es nicht (Name nicht gefunden oder falsch geschrieben).";
                case PingState.NoAnswer: return "Keine Antwort über UDP: Server aus, falscher Port, oder Router/Firewall lassen ihn nicht durch.";
            }
            var info = ping.Info;
            if (!info.IsCompatible) return $"Der Server antwortet, passt aber nicht zu diesem Spiel (Protokoll {info.ProtocolVersion} statt {ProtocolInfo.Version}).";
            return $"\"{info.Name}\", Map {info.Map}, {info.Players}/{info.MaxPlayers} Spieler, Ping {ping.RoundTripMs} ms";
        }

        private void Save()
        {
            PlayerPrefs.SetString(PrefName, _name ?? "");
            PlayerPrefs.SetString(PrefAddress, _address ?? "");
            if (_devTicketChosen) PlayerPrefs.SetInt(PrefDevTicket, _devTicket ? 1 : 0);
            PlayerPrefs.SetInt(PrefTab, _tab);
            PlayerPrefs.Save();
        }

        private void CreateStyles()
        {
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 15, clipping = TextClipping.Clip };
            _note ??= new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            _header ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _row ??= new GUIStyle(GUI.skin.box) { margin = new RectOffset(0, 0, 1, 1) };
            if (_rowSelected == null)
            {
                _rowSelected = new GUIStyle(_row);
                var tint = new Texture2D(1, 1);
                tint.SetPixel(0, 0, new Color(0.25f, 0.45f, 0.8f, 0.6f));
                tint.Apply();
                _rowSelected.normal.background = tint;
                _rowSelected.hover.background = tint;
            }
        }
    }
}
