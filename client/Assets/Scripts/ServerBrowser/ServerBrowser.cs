using System;
using System.Collections;
using System.Collections.Generic;
using AoS2.Client.Account;
using AoS2.Contracts;
using AoS2.Net;
using AoS2.Shared;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AoS2.Client
{
    /// <summary>
    /// Server-Browser ohne Darstellung (die zeichnet das <see cref="StartMenu"/>): lädt die Liste vom Backend
    /// (<c>GET /api/servers</c>, nur die eigene Protokollversion), misst den Ping zu jedem Server direkt per UDP
    /// (<see cref="ServerPinger"/>) und wendet Filter und Sortierung an (<see cref="ServerList"/>).
    /// <para>Aktualisiert sich selbst, solange das Menü offen ist. Die Filter merkt es sich in den PlayerPrefs.</para>
    /// </summary>
    public sealed class ServerBrowser : MonoBehaviour
    {
        /// <summary>So oft wird die Liste neu geladen, solange das Menü offen ist (Server melden sich alle 30 s).</summary>
        public const float RefreshSeconds = 30f;

        private const int TimeoutSeconds = 10;
        private const string PrefHideFull = "aos2.browser.hideFull";
        private const string PrefHideEmpty = "aos2.browser.hideEmpty";
        private const string PrefSort = "aos2.browser.sort";

        private readonly ServerList _list = new ServerList();
        private ServerPinger _pinger;
        private float _nextRefresh;
        private string _siteUrl;

        public static ServerBrowser Instance { get; private set; }

        public ServerFilter Filter { get; } = new ServerFilter();

        /// <summary>Gerade geladen wird die Liste.</summary>
        public bool IsLoading { get; private set; }

        /// <summary>Hat die Liste schon einmal geladen.</summary>
        public bool HasList { get; private set; }

        /// <summary>Fehler beim Laden, sonst <c>null</c>.</summary>
        public string Error { get; private set; }

        /// <summary>Website, deren Backend die Liste liefert. Ohne Angabe die aus der Anmeldung (<see cref="GameSession.SiteUrl"/>).</summary>
        public string SiteUrl
        {
            get => _siteUrl ?? GameSession.SiteUrl;
            set => _siteUrl = value?.TrimEnd('/');
        }

        /// <summary>Soll sich die Liste gerade aktualisieren (Menü offen)? Setzt das <see cref="StartMenu"/>.</summary>
        public bool Active { get; set; }

        public IReadOnlyList<string> Maps => _list.Maps;

        /// <summary>Alle passenden Server vom Backend (vor den Filtern).</summary>
        public int Total => _list.Entries.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject(nameof(ServerBrowser));
            go.AddComponent<ServerBrowser>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            Instance = this;
            _pinger = new ServerPinger(() => Time.realtimeSinceStartupAsDouble);
            Filter.HideFull = PlayerPrefs.GetInt(PrefHideFull, 0) == 1;
            Filter.HideEmpty = PlayerPrefs.GetInt(PrefHideEmpty, 0) == 1;
            Filter.Sort = (ServerSort)Mathf.Clamp(PlayerPrefs.GetInt(PrefSort, 0), 0, 2);
        }

        private void OnDestroy()
        {
            _pinger?.Dispose();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!Active) return;
            _pinger.Poll();
            if (!IsLoading && Time.unscaledTime >= _nextRefresh) Refresh();
        }

        /// <summary>Was das Menü anzeigt: gefiltert, sortiert, mit Ping.</summary>
        public IReadOnlyList<ServerRow> Rows => _list.Visible(Filter, _pinger.ResultFor);

        /// <summary>Liste neu laden und alle Server neu anpingen.</summary>
        public void Refresh()
        {
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            if (IsLoading) return;
            if (string.IsNullOrEmpty(SiteUrl))
            {
                Error = "Serverliste nicht verfügbar: Das Spiel wurde ohne Launcher gestartet. \"Direkt verbinden\" geht trotzdem.";
                return;
            }
            StartCoroutine(Load());
        }

        /// <summary>Einen Server (z. B. die Adresse aus „Direkt verbinden“) anpingen.</summary>
        public void Ping(string address) => _pinger.Ping(address);

        public PingResult PingOf(string address) => _pinger.ResultFor(address);

        /// <summary>Filter speichern (nach jeder Änderung im Menü).</summary>
        public void SaveFilter()
        {
            PlayerPrefs.SetInt(PrefHideFull, Filter.HideFull ? 1 : 0);
            PlayerPrefs.SetInt(PrefHideEmpty, Filter.HideEmpty ? 1 : 0);
            PlayerPrefs.SetInt(PrefSort, (int)Filter.Sort);
            PlayerPrefs.Save();
        }

        private IEnumerator Load()
        {
            IsLoading = true;
            var url = SiteUrl + ApiRoutes.Servers + "?protocol=" + ProtocolInfo.Version;
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();
                IsLoading = false;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Error = $"Serverliste nicht erreichbar ({SiteUrl}): {request.error}";
                    yield break;
                }

                ServerListResponse response;
                try
                {
                    response = JsonConvert.DeserializeObject<ServerListResponse>(request.downloadHandler.text);
                }
                catch (JsonException e)
                {
                    Error = "Serverliste unlesbar: " + e.Message;
                    yield break;
                }
                _list.Set(response?.Servers);
                Error = null;
                HasList = true;
                if (Filter.Map != null && !ContainsMap(Filter.Map)) Filter.Map = null;
                foreach (var entry in _list.Entries) _pinger.Ping(entry.Address);
            }
        }

        private bool ContainsMap(string map)
        {
            foreach (var m in _list.Maps)
                if (string.Equals(m, map, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
