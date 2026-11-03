using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AoS2.Net;
using AoS2.Shared;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace AoS2.Client.Tests
{
    /// <summary>
    /// Server-Browser im Spiel: lädt die Liste über HTTP (hier eine nachgebildete <c>/api/servers</c>), blendet Server mit
    /// anderer Protokollversion aus und misst den Ping zum echten Spielserver (<c>dotnet AoS2.Server.dll --dev</c>) per UDP.
    /// Braucht einen gebauten Server: <c>dotnet build server/AoS2.Server</c>.
    /// </summary>
    public class ServerBrowserTests
    {
        private Process _server;
        private HttpListener _http;
        private Thread _httpThread;
        private string _config;
        private int _port;
        private int _deadPort;
        private int _requests;

        [UnitySetUp]
        public IEnumerator StartServerAndList()
        {
            var dll = OnlineGameTests.FindServer();
            if (dll == null) Assert.Ignore("Server nicht gebaut (dotnet build server/AoS2.Server).");
            _port = OnlineGameTests.FreeUdpPort();
            _deadPort = OnlineGameTests.FreeUdpPort();
            _config = Path.Combine(Path.GetTempPath(), $"aos2-browser-{_port}.json");
            File.WriteAllText(_config, "{ \"dev\": true, \"port\": " + _port + ", \"name\": \"PlayMode-Server\", \"playersPerTeam\": 4 }");
            _server = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" --config \"{_config}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            _server.OutputDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[Server] " + e.Data); };
            _server.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.Log("[Server] " + e.Data); };
            _server.BeginOutputReadLine();
            _server.BeginErrorReadLine();

            StartFakeBackend();

            GameClient.StartOfflineImmediately = false;
            var loading = SceneManager.LoadSceneAsync("Game");
            while (!loading.isDone) yield return null;
            yield return null;
            Assert.IsNotNull(ServerBrowser.Instance, "Server-Browser erzeugt sich selbst");
            Assert.AreEqual(GameMode.Menu, GameClient.Instance.Mode);
        }

        [UnityTearDown]
        public IEnumerator Stop()
        {
            StartMenu.Tab = -1;
            var browser = ServerBrowser.Instance;
            if (browser != null)
            {
                browser.SiteUrl = null;
                browser.Filter.HideEmpty = false;
                browser.Filter.HideFull = false;
                browser.Filter.Map = null;
            }
            yield return null;
            _http?.Close();
            if (_server != null && !_server.HasExited) _server.Kill();
            _server?.Dispose();
            if (_config != null && File.Exists(_config)) File.Delete(_config);
        }

        [UnityTest]
        public IEnumerator Browser_ListsCompatibleServers_WithPingMeasuredDirectly()
        {
            var browser = ServerBrowser.Instance;
            browser.SiteUrl = _http.Prefixes.First();
            StartMenu.Tab = 0; // Reiter Server-Browser: aktualisiert sich selbst
            yield return null;
            browser.Refresh();

            // Der Server braucht einen Moment zum Starten; bis dahin fragt der Browser beim nächsten Aktualisieren erneut.
            var address = "127.0.0.1:" + _port;
            var timeout = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < timeout)
            {
                var ping = browser.PingOf(address);
                if (ping != null && ping.State == PingState.Answered) break;
                if (ping != null && ping.State == PingState.NoAnswer) browser.Refresh();
                yield return null;
            }

            Assert.IsNull(browser.Error, browser.Error);
            Assert.GreaterOrEqual(_requests, 1, "Liste über HTTP geladen");
            var rows = browser.Rows;
            var names = rows.Select(r => r.Entry.Name).ToList();
            CollectionAssert.DoesNotContain(names, "Alte Version", "andere Protokollversion ist ausgeblendet");
            CollectionAssert.Contains(names, "Stumm");

            var row = rows.Single(r => r.Entry.Address == address);
            Assert.AreEqual(PingState.Answered, row.Ping.State, "Ping per UDP");
            Assert.IsNotNull(row.PingMs);
            Assert.Less(row.PingMs.Value, 500);
            Assert.AreEqual(("PlayMode-Server", "testmap", 0, 8), (row.Ping.Info.Name, row.Ping.Info.Map, row.Players, row.MaxPlayers));

            // Stummer Server: nach allen Versuchen „keine Antwort“, aber weiter in der Liste.
            timeout = Time.realtimeSinceStartup + 6f;
            while (browser.PingOf("127.0.0.1:" + _deadPort).State == PingState.Pending && Time.realtimeSinceStartup < timeout) yield return null;
            Assert.AreEqual(PingState.NoAnswer, browser.PingOf("127.0.0.1:" + _deadPort).State);

            // Filter: Leere ausblenden (beide Server sind leer), Map-Filter.
            browser.Filter.HideEmpty = true;
            Assert.AreEqual(0, browser.Rows.Count);
            browser.Filter.HideEmpty = false;
            browser.Filter.Map = "insel";
            Assert.AreEqual(0, browser.Rows.Count);
            browser.Filter.Map = "testmap";
            Assert.AreEqual(2, browser.Rows.Count);
        }

        [UnityTest]
        public IEnumerator DirectConnect_CheckButton_PingsTypedAddress()
        {
            var browser = ServerBrowser.Instance;
            var address = "127.0.0.1:" + _port;
            var timeout = Time.realtimeSinceStartup + 30f;
            browser.Ping(address);
            StartMenu.Tab = 0; // der Browser fragt nur, solange er aktiv ist
            while (Time.realtimeSinceStartup < timeout)
            {
                var ping = browser.PingOf(address);
                if (ping.State == PingState.Answered) break;
                if (ping.State == PingState.NoAnswer) browser.Ping(address); // Server startet noch
                yield return null;
            }

            var result = browser.PingOf(address);
            Assert.AreEqual(PingState.Answered, result.State);
            Assert.IsTrue(result.Info.IsCompatible);
            Assert.AreEqual("PlayMode-Server", result.Info.Name);
        }

        /// <summary>Nachgebildetes <c>GET /api/servers</c>: der echte Server, einer mit alter Protokollversion und einer, der nicht antwortet.</summary>
        private void StartFakeBackend()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var tcpPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _http = new HttpListener();
            _http.Prefixes.Add($"http://127.0.0.1:{tcpPort}/");
            _http.Start();

            var json = "{ \"servers\": ["
                + Entry("127.0.0.1:" + _port, "PlayMode-Server", ProtocolInfo.Version) + ","
                + Entry("127.0.0.1:" + _deadPort, "Stumm", ProtocolInfo.Version) + ","
                + Entry("127.0.0.1:1", "Alte Version", ProtocolInfo.Version - 1)
                + "] }";
            var body = Encoding.UTF8.GetBytes(json);
            _httpThread = new Thread(() =>
            {
                while (_http.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = _http.GetContext();
                    }
                    catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                    {
                        return;
                    }
                    var ok = context.Request.Url.AbsolutePath == "/api/servers" && context.Request.QueryString["protocol"] == ProtocolInfo.Version.ToString();
                    if (ok) Interlocked.Increment(ref _requests);
                    context.Response.StatusCode = ok ? 200 : 404;
                    context.Response.ContentType = "application/json";
                    if (ok) context.Response.OutputStream.Write(body, 0, body.Length);
                    context.Response.Close();
                }
            }) { IsBackground = true };
            _httpThread.Start();
        }

        private static string Entry(string address, string name, int protocol) =>
            $"{{ \"address\": \"{address}\", \"name\": \"{name}\", \"map\": \"testmap\", \"players\": 0, \"maxPlayers\": 8, " +
            $"\"protocolVersion\": {protocol}, \"gameVersion\": \"0.0.0\", \"official\": false }}";
    }
}
