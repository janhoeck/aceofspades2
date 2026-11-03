using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using AoS2.Net;
using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace AoS2.Client.Tests
{
    /// <summary>
    /// Ende-zu-Ende: startet den echten Spielserver (<c>dotnet AoS2.Server.dll --dev</c>) und verbindet die Spielszene damit.
    /// Ein zweiter Client (ohne Darstellung, gleicher Code wie im Spiel) prüft, dass Bewegung und Blockänderungen ankommen.
    /// Braucht einen gebauten Server: <c>dotnet build server/AoS2.Server</c>.
    /// </summary>
    public class OnlineGameTests
    {
        private Process _server;
        private int _port;
        private GameClient _game;
        private NetClient _otherNet;
        private ClientSession _other;
        private double _otherNextTick;
        private PlayerInput _otherInput;

        [UnitySetUp]
        public IEnumerator StartServerAndConnect()
        {
            var dll = FindServer();
            if (dll == null) Assert.Ignore("Server nicht gebaut (dotnet build server/AoS2.Server).");
            _port = FreeUdpPort();
            _server = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" --dev --port {_port}")
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

            GameClient.StartOfflineImmediately = false;
            var loading = SceneManager.LoadSceneAsync("Game");
            while (!loading.isDone) yield return null;
            yield return null; // Start() des GameClient
            _game = GameClient.Instance;
            Assert.IsNotNull(_game);
            Assert.IsNull(_game.LoadError);
            Assert.AreEqual(GameMode.Menu, _game.Mode, "ohne -connect startet das Spiel im Menü");

            // Der Server braucht einen Moment zum Starten; bis dahin schlägt das Verbinden fehl, dann erneut versuchen.
            var timeout = Time.realtimeSinceStartup + 30f;
            while (!_game.IsReady && Time.realtimeSinceStartup < timeout)
            {
                if (_game.Mode == GameMode.Menu) _game.Connect("127.0.0.1:" + _port, "Unity", true);
                yield return null;
            }
            Assert.IsTrue(_game.IsReady, "verbunden: " + _game.Status);
            Assert.AreEqual(GameMode.Online, _game.Mode);
        }

        [UnityTearDown]
        public IEnumerator Stop()
        {
            if (_game != null && _game.Mode != GameMode.Menu) _game.LeaveToMenu();
            _otherNet?.Disconnect();
            _otherNet?.Dispose();
            yield return null;
            if (_server != null && !_server.HasExited) _server.Kill();
            _server?.Dispose();
        }

        [UnityTest]
        public IEnumerator Online_MovementAndBlocks_ReachOtherClient()
        {
            ConnectOtherClient();
            var timeout = Time.realtimeSinceStartup + 10f;
            while ((_other.Phase != ClientPhase.Playing || _game.Session.RemotePlayers.Count == 0) && Time.realtimeSinceStartup < timeout)
            {
                UpdateOther();
                yield return null;
            }
            Assert.AreEqual(ClientPhase.Playing, _other.Phase, _other.Error);
            Assert.AreEqual(1, _game.Session.RemotePlayers.Count, "die Spielszene sieht den zweiten Client");

            // Unity-Spieler läuft eine Sekunde vorwärts.
            var start = _game.Player.State.Position;
            _game.Player.TestButtons = InputButtons.Forward;
            yield return Run(1f);
            _game.Player.TestButtons = null;
            yield return Run(0.7f);

            var moved = _game.Player.State.Position;
            Assert.Greater(Distance(start, moved), 2f, "gelaufen");
            Assert.IsTrue(_other.RemotePlayers.TryGetValue(_game.Session.PlayerId, out var seen));
            Assert.Less(Distance(seen.Current.Position, moved), 0.01f, "der andere Client sieht den Unity-Spieler dort, wo er steht");
            Assert.AreEqual(0, _game.Session.Prediction.Mispredictions, "Vorhersage stimmte immer");
            Assert.AreEqual(1, _game.RemotePlayers.Count, "Avatar des anderen wird gezeigt");

            // Zum anderen Spieler schauen und ein Bild machen (artifacts/screenshots/m4-online.png).
            var other = _other.Prediction.State.Position;
            float dx = other.X - moved.X, dz = other.Z - moved.Z, dy = other.Y + 1f - (moved.Y + _game.Movement.EyeHeight(false));
            _game.Player.TestLook(Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360), -Mathf.Atan2(dy, Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg);
            yield return Run(0.3f);
            yield return Screenshot("m4-online");

            // Schräg nach unten schauen und einen Block bauen, wie mit Rechtsklick.
            _game.Player.TestLook(0, 50);
            yield return Run(0.1f);
            var target = _game.Player.Target;
            Assert.IsTrue(target.HasValue, "Boden vor den Füßen anvisiert");
            int x = target.Value.PlaceX, y = target.Value.PlaceY, z = target.Value.PlaceZ;
            var build = _game.Player.SelectedBlock.Id;
            _game.Player.TestEdit(remove: false);
            yield return Run(0.5f);
            Assert.AreEqual(build, _game.World.GetBlock(x, y, z), "Unity-Client");
            Assert.AreEqual(build, _other.World.GetBlock(x, y, z), "der andere Client bekommt den Block vom Server");
            Assert.AreEqual(0, _game.Session.Blocks.PendingCount, "Server hat bestätigt");

            // Und wieder abbauen
            _game.Player.TestEdit(remove: true);
            yield return Run(0.5f);
            Assert.IsFalse(_game.World.IsSolid(x, y, z));
            Assert.IsFalse(_other.World.IsSolid(x, y, z));
        }

        /// <summary>
        /// M5: Der Unity-Spieler nimmt das Gewehr und hält auf den zweiten Client, so wie er ihn sieht, bis der Server ihn
        /// für tot erklärt. Geprüft wird das ganze Paket: Vorhersage feuert, Server trifft (Lag-Ausgleich), Trefferanzeige und
        /// Kill-Anzeige im Spiel, Spielermodell sichtbar, Respawn nach 5 s. Bilder: artifacts/screenshots/m5-*.png.
        /// </summary>
        [UnityTest]
        public IEnumerator Online_ShootOtherPlayer_UntilKilled_ThenRespawn()
        {
            ConnectOtherClient();
            var kills = new List<WorldEvent>();
            _other.CombatEvent += e => { if (e.Type == WorldEventType.PlayerKilled) kills.Add(e); };
            var timeout = Time.realtimeSinceStartup + 10f;
            while ((_other.Phase != ClientPhase.Playing || _game.RemotePlayers.Count == 0) && Time.realtimeSinceStartup < timeout)
            {
                UpdateOther();
                yield return null;
            }
            Assert.AreEqual(1, _game.RemotePlayers.Count, "Spielermodell des anderen wird gezeigt");

            // Der andere hält ebenfalls das Gewehr und schaut zum Unity-Spieler: Zielhaltung im Bild.
            var me = _game.Player.State.Position;
            var him = _other.Prediction.State.Position;
            _otherInput = new PlayerInput { Weapon = 1, Yaw = Mathf.Repeat(Mathf.Atan2(me.X - him.X, me.Z - him.Z) * Mathf.Rad2Deg, 360), Pitch = 10 };
            _game.Player.TestSelectSlot(1);
            AimAtOther(16f / 16);
            yield return Run(0.6f);
            Assert.AreEqual(1, _game.Player.Slot);
            Assert.IsTrue(_game.Session.Prediction.Weapons.Switch == 0, "Waffe bereit");
            yield return Screenshot("m5-model");

            _game.Player.TestFire(true);
            timeout = Time.realtimeSinceStartup + 5f;
            while (kills.Count == 0 && Time.realtimeSinceStartup < timeout)
            {
                AimAtOther(16f / 16);
                UpdateOther();
                yield return null;
            }
            _game.Player.TestFire(false);
            Assert.AreEqual(1, kills.Count, "der Server meldet den Kill");
            Assert.AreEqual(_game.Session.PlayerId, kills[0].Attacker);
            Assert.IsFalse(_other.Alive);
            Assert.GreaterOrEqual(_game.Player.ShotsFired, 4, "4 Rumpftreffer à 25");
            yield return Run(0.3f);
            Assert.GreaterOrEqual(_game.Feed.HitsConfirmed, 4, "Trefferanzeige: Server hat die Treffer bestätigt");
            Assert.AreEqual(1, _game.Feed.Kills.Count, "Kill-Anzeige");
            Assert.AreEqual(0, _game.Session.Prediction.Mispredictions, "Waffen-Vorhersage stimmte");
            Assert.Less(_game.Session.Prediction.Weapons.GetAmmo(1).Magazine, 30);
            Assert.AreEqual(0, _game.RemotePlayers.Avatars.Count(), "Tote werden ausgeblendet");
            yield return Screenshot("m5-kill");

            // Granate zur Probe: fliegt sichtbar und explodiert (Effekte ohne Fehler)
            _game.Player.TestSelectSlot(2);
            yield return Run(0.5f);
            _game.Player.TestLook(_game.Player.Yaw, 30);
            _game.Player.TestFire(true);
            yield return Run(0.1f);
            _game.Player.TestFire(false);
            timeout = Time.realtimeSinceStartup + 2f;
            while (_game.Session.Projectiles.All.Count == 0 && Time.realtimeSinceStartup < timeout) yield return Run(0.05f);
            Assert.AreEqual(1, _game.Session.Projectiles.All.Count, "Granate fliegt");
            yield return Run(0.3f);
            yield return Screenshot("m5-grenade");
            timeout = Time.realtimeSinceStartup + 4f;
            while (_game.Session.Projectiles.All.Count > 0 && Time.realtimeSinceStartup < timeout) yield return Run(0.05f);
            yield return null;
            Assert.Greater(_game.Effects.ActiveParticles, 10, "Explosion zu sehen");
            yield return Screenshot("m5-explosion");

            timeout = Time.realtimeSinceStartup + 7f;
            while (!_other.Alive && Time.realtimeSinceStartup < timeout) yield return Run(0.1f);
            Assert.IsTrue(_other.Alive, "Respawn nach 5 s");
            Assert.AreEqual(100, _other.Health);
        }

        /// <summary>Blick auf den anderen Spieler, so wie die Spielszene ihn gerade zeigt (Höhe über den Füßen in Blöcken).</summary>
        private void AimAtOther(float height)
        {
            if (!_game.Session.RemotePlayers.TryGetValue(_other.PlayerId, out var seen) || !seen.HasState) return;
            var target = seen.Current.Position;
            var eye = _game.Player.State.Eye(_game.Movement);
            float dx = target.X - eye.X, dz = target.Z - eye.Z, dy = target.Y + height - eye.Y;
            _game.Player.TestLook(Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360), -Mathf.Atan2(dy, Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg);
        }

        private void ConnectOtherClient()
        {
            _otherInput = default;
            var registry = _game.Registry;
            _otherNet = new NetClient();
            _other = new ClientSession(registry, _game.Movement,
                name => File.ReadAllBytes(Path.Combine(GameDataPaths.MapsFolder, name + MapFile.Extension)),
                _otherNet, () => Time.realtimeSinceStartupAsDouble, _game.Combat);
            _otherNet.Session = _other;
            ServerAddress.TryParse("127.0.0.1:" + _port, out var address);
            _otherNet.Connect(address, DevJoinTicket.Create(Guid.NewGuid(), "Zweiter", DateTimeOffset.UtcNow));
            _otherNextTick = Time.realtimeSinceStartupAsDouble;
        }

        private void UpdateOther()
        {
            _otherNet.Poll();
            _other.Update();
            while (Time.realtimeSinceStartupAsDouble >= _otherNextTick)
            {
                _otherNextTick += 1.0 / 60;
                _other.Tick(_otherInput);
            }
        }

        private IEnumerator Run(float seconds)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                UpdateOther();
                yield return null;
            }
        }

        private static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        private static IEnumerator Screenshot(string name)
        {
            yield return null; // WaitForEndOfFrame kommt im Batchmode nie
            var camera = Camera.main;
            var target = new RenderTexture(1280, 720, 24);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            camera.targetTexture = null;
            RenderTexture.active = null;

            var folder = Path.Combine(GameDataPaths.RepositoryRoot, "artifacts", "screenshots");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            UnityEngine.Object.Destroy(target);
        }

        private static string FindServer()
        {
            foreach (var config in new[] { "debug", "release" })
            {
                var path = Path.Combine(GameDataPaths.RepositoryRoot, "artifacts", "bin", "AoS2.Server", config, "AoS2.Server.dll");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static int FreeUdpPort()
        {
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }
    }
}
