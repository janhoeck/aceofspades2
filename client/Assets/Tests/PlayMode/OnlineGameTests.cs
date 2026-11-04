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
        private Team _otherTeam;
        private string _otherClass;
        private bool _otherTeamRequested;
        private string _config;

        [UnitySetUp]
        public IEnumerator StartServerAndConnect()
        {
            var dll = FindServer();
            if (dll == null) Assert.Ignore("Server nicht gebaut (dotnet build server/AoS2.Server).");
            _port = FreeUdpPort();
            // Server-Config wie beim Self-Hosting. Im Match-Test gewinnt schon ein Punkt, danach kurze Pause bis zum Neustart.
            var scoreLimit = TestContext.CurrentContext.Test.Name.Contains("WinsMatch") ? 1 : 50;
            _config = Path.Combine(Path.GetTempPath(), $"aos2-playmode-{_port}.json");
            File.WriteAllText(_config, "{ \"dev\": true, \"port\": " + _port + ", \"scoreLimit\": " + scoreLimit + ", \"matchEndSeconds\": 3 }");
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

            // M6: Nach dem Beitritt ist man Zuschauer, das Team-Menü ist offen. Blau wählen: Man erscheint im blauen Spawn.
            Assert.IsTrue(_game.TeamMenuOpen, "Team-Menü nach dem Beitritt");
            Assert.AreEqual(Team.None, _game.Session.Team);
            _game.ChooseTeam(Team.Blue);
            timeout = Time.realtimeSinceStartup + 5f;
            while ((_game.Session.Team != Team.Blue || !_game.Session.Alive) && Time.realtimeSinceStartup < timeout) yield return null;
            Assert.AreEqual(Team.Blue, _game.Session.Team);
            Assert.IsTrue(_game.Session.Alive, "gespawnt");
            Assert.IsFalse(_game.TeamMenuOpen, "Menü schließt nach der Wahl");
            yield return null;
            Assert.IsTrue(_game.Session.Zones.InSpawnZone(_game.Player.State.Position), "im eigenen Spawn");
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
            if (_config != null && File.Exists(_config)) File.Delete(_config);
        }

        [UnityTest]
        public IEnumerator Online_MovementAndBlocks_ReachOtherClient()
        {
            ConnectOtherClient(Team.Green);
            yield return WaitForOther();
            Assert.AreEqual(1, _game.Session.RemotePlayers.Count, "die Spielszene sieht den zweiten Client");

            // Im Spawn darf man nichts bauen; dafür erst in die Kampfzone laufen, dem anderen entgegen.
            yield return MeetInTheMiddle();
            var mispredictions = _game.Session.Prediction.Mispredictions; // der Sprung zum Spawn beim Teambeitritt zählt nicht

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
            Assert.AreEqual(mispredictions, _game.Session.Prediction.Mispredictions, "Vorhersage stimmte immer");
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
            ConnectOtherClient(Team.Green);
            var kills = new List<WorldEvent>();
            _other.CombatEvent += e => { if (e.Type == WorldEventType.PlayerKilled) kills.Add(e); };
            yield return WaitForOther();
            Assert.AreEqual(1, _game.RemotePlayers.Count, "Spielermodell des anderen wird gezeigt");

            // Im Spawn feuert keine Waffe: erst hinaus, dem Gegner entgegen.
            _game.Player.TestSelectSlot(1);
            yield return Run(0.5f);
            _game.Player.TestFire(true);
            yield return Run(0.3f);
            _game.Player.TestFire(false);
            Assert.AreEqual(0, _game.Player.ShotsFired, "im Spawn kein Schuss");
            yield return MeetInTheMiddle();
            var mispredictions = _game.Session.Prediction.Mispredictions;
            var timeout = 0f;

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
            Assert.AreEqual(mispredictions, _game.Session.Prediction.Mispredictions, "Waffen-Vorhersage stimmte");
            Assert.Less(_game.Session.Prediction.Weapons.GetAmmo(1).Magazine, 30);
            Assert.AreEqual(0, _game.RemotePlayers.Avatars.Count(), "Tote werden ausgeblendet");
            yield return Screenshot("m5-kill");

            // Granate zur Probe: fliegt sichtbar und explodiert (Effekte ohne Fehler). Assault: Platz 4 (Taste 4).
            _game.Player.TestSelectSlot(3);
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

        /// <summary>
        /// M6, ein ganzes Match (die Server-Config setzt das Punkteziel auf 1): Blau gegen Grün, beide laufen aus ihrem Spawn
        /// aufeinander zu, der Unity-Spieler schießt den Gegner ab. Der Punkt beendet das Match, beide Clients zeigen den Sieger,
        /// nach 3 s beginnt das nächste Match mit 0 : 0 und alle stehen wieder im Spawn. Bilder: artifacts/screenshots/m6-*.png.
        /// </summary>
        [UnityTest]
        public IEnumerator Online_BlueVsGreen_KillWinsMatch_ThenRestart()
        {
            ConnectOtherClient(Team.Green);
            yield return WaitForOther();
            Assert.AreEqual(Team.Green, _game.Session.RemotePlayers[_other.PlayerId].Team);
            Assert.AreEqual(Team.Green, _game.RemotePlayers.ShownTeam(_other.PlayerId), "Gegner in Grün");
            Assert.AreEqual(1, _game.Session.TeamSize(Team.Blue));
            Assert.AreEqual(1, _game.Session.TeamSize(Team.Green));
            Assert.IsFalse(_game.CanJoin(Team.Green), "ein Wechsel würde Grün größer machen");

            yield return MeetInTheMiddle();
            Assert.IsFalse(_game.Session.Zones.InSpawnZone(_game.Player.State.Position));
            Assert.IsFalse(_game.Session.Zones.InSpawnZone(_other.Prediction.State.Position));

            // Grün schaut zu Blau (Bild mit beiden Teamfarben), Blau schießt.
            var me = _game.Player.State.Position;
            var him = _other.Prediction.State.Position;
            _otherInput = new PlayerInput { Weapon = 1, Yaw = Mathf.Repeat(Mathf.Atan2(me.X - him.X, me.Z - him.Z) * Mathf.Rad2Deg, 360) };
            _game.Player.TestSelectSlot(1);
            AimAtOther(16f / 16);
            yield return Run(0.6f);
            yield return Screenshot("m6-enemy");
            _game.Player.TestFire(true);
            var timeout = Time.realtimeSinceStartup + 5f;
            while (!_game.Session.MatchOver && Time.realtimeSinceStartup < timeout)
            {
                AimAtOther(16f / 16);
                UpdateOther();
                yield return null;
            }
            _game.Player.TestFire(false);

            // Punkt für Blau, Match vorbei: Beide sehen den Sieger.
            Assert.IsTrue(_game.Session.MatchOver, "Match vorbei");
            Assert.AreEqual(1, _game.Session.Match.ScoreBlue);
            Assert.AreEqual(Team.Blue, _game.Session.Match.Winner);
            Assert.AreEqual(1, _game.Session.Kills);
            yield return Run(0.3f);
            Assert.IsTrue(_other.MatchOver);
            Assert.AreEqual(Team.Blue, _other.Match.Winner);
            Assert.AreEqual(1, _other.Deaths);
            Assert.AreEqual(1, _other.RemotePlayers[_game.Session.PlayerId].Kills);
            yield return Screenshot("m6-match-end");

            // Nach 3 s: nächstes Match.
            timeout = Time.realtimeSinceStartup + 6f;
            while (_game.Session.MatchOver && Time.realtimeSinceStartup < timeout) yield return Run(0.1f);
            Assert.IsFalse(_game.Session.MatchOver, "neues Match");
            yield return Run(0.5f);
            Assert.AreEqual(0, _game.Session.Match.ScoreBlue);
            Assert.AreEqual(2, _game.Session.Match.MatchNumber);
            Assert.AreEqual(0, _game.Session.Kills);
            Assert.IsTrue(_game.Session.Alive && _other.Alive, "alle wieder da");
            Assert.IsTrue(_game.Session.Zones.SpawnZoneAt(_game.Player.State.Position) == Team.Blue, "Unity-Spieler im blauen Spawn");
            Assert.IsTrue(_game.Session.Zones.SpawnZoneAt(_other.Prediction.State.Position) == Team.Green, "Gegner im grünen Spawn");
            Assert.AreEqual(Team.Blue, _game.Session.Team, "Teams bleiben");
        }

        /// <summary>
        /// M8, Klassen: Der zweite Client (Grün) wählt vor dem Spawn den Rocketeer, die Spielszene zeigt ihn mit dessen
        /// Ausrüstung. Der Unity-Spieler (Assault) wählt im Menü den Recon: Er bleibt Assault, bis er stirbt (hier durch die
        /// eigene Granate), und erscheint dann als Recon mit Scharfschützengewehr und Zielfernrohr.
        /// Bild: artifacts/screenshots/m8-rocketeer.png.
        /// </summary>
        [UnityTest]
        public IEnumerator Online_ClassChoice_GearWeaponsAndRespawnRule()
        {
            ConnectOtherClient(Team.Green, "rocketeer");
            yield return WaitForOther();
            Assert.AreEqual("rocketeer", _other.Class.Name, "zweiter Client spawnt mit der gewählten Klasse");
            Assert.AreEqual("rocketeer", _game.Session.ClassOf(_other.PlayerId).Name);
            Assert.AreEqual("rocketeer", _game.RemotePlayers.ShownGear(_other.PlayerId), "Modell mit Rocketeer-Ausrüstung");
            var rocketeerBoxes = _game.Rig.Boxes.Count(b => b.Gear == null || b.Gear == "rocketeer");
            Assert.AreEqual(rocketeerBoxes, _game.RemotePlayers.Model(_other.PlayerId).BoxCount, "genau die Würfel, die der Server prüft");
            Assert.AreEqual(1.982f, _other.Movement.StandHeight, 0.001f, "Kollisionshöhe mit Raketenhelm");

            // Beide gehen aus dem Spawn, der andere schaut den Unity-Spieler an, mit dem Raketenwerfer in der Hand (Bild).
            yield return MeetInTheMiddle();
            var me = _game.Player.State.Position;
            var him = _other.Prediction.State.Position;
            _otherInput = new PlayerInput { Weapon = 1, Yaw = Mathf.Repeat(Mathf.Atan2(me.X - him.X, me.Z - him.Z) * Mathf.Rad2Deg, 360) };
            AimAtOther(20f / 16);
            yield return Run(0.8f);
            yield return Screenshot("m8-rocketeer");

            // Klassenwahl im Menü: Der Unity-Spieler lebt, also gilt die neue Klasse erst beim nächsten Spawn.
            Assert.AreEqual("assault", _game.Session.Class.Name);
            _game.OpenClassMenu();
            Assert.IsTrue(_game.TeamMenuOpen && _game.ClassMenuOnly, "K öffnet die Klassenwahl");
            _game.ChooseClass(_game.Session.Classes.Find("recon"));
            Assert.IsFalse(_game.TeamMenuOpen, "Menü schließt nach der Wahl");
            yield return Run(0.5f);
            Assert.AreEqual("assault", _game.Session.Class.Name, "bleibt bis zum nächsten Spawn");
            Assert.AreEqual("recon", _game.MenuClass.Name);

            // Granate vor die eigenen Füße (Assault: Platz 4): Nach 3 s tot, nach weiteren 5 s wieder da, jetzt als Recon.
            _otherInput = default;
            _game.Player.TestSelectSlot(3);
            yield return Run(0.6f);
            _game.Player.TestLook(_game.Player.Yaw, 89);
            _game.Player.TestFire(true);
            yield return Run(0.1f);
            _game.Player.TestFire(false);
            var timeout = Time.realtimeSinceStartup + 5f;
            while (_game.Session.Alive && Time.realtimeSinceStartup < timeout) yield return Run(0.1f);
            Assert.IsFalse(_game.Session.Alive, "von der eigenen Granate erwischt");
            Assert.AreEqual("assault", _game.Session.Class.Name, "tot, aber noch nicht wieder da");
            timeout = Time.realtimeSinceStartup + 8f;
            while (!_game.Session.Alive && Time.realtimeSinceStartup < timeout) yield return Run(0.1f);
            Assert.IsTrue(_game.Session.Alive, "wieder da");
            yield return Run(0.3f);
            Assert.AreEqual("recon", _game.Session.Class.Name);
            Assert.AreEqual(_game.Session.Classes.Find("recon").Movement.SprintSpeed, _game.Movement.SprintSpeed, "Recon läuft schneller");
            Assert.AreEqual("recon", _other.ClassOf(_game.Session.PlayerId).Name, "der andere sieht die neue Klasse");
            Assert.AreEqual(30, _game.Session.BlocksLeft);

            _game.Player.TestSelectSlot(1);
            yield return Run(0.5f);
            Assert.AreEqual("sniper", _game.Player.Weapon.Name);
            Assert.AreEqual(5, _game.Session.Prediction.Weapons.GetAmmo(1).Magazine);
            _game.Player.TestAim(true);
            yield return Run(0.5f);
            Assert.IsTrue(_game.Player.Zoomed);
            Assert.Less(Camera.main.fieldOfView, 30f, "Zielfernrohr vergrößert (Sichtfeld kleiner)");
            _game.Player.TestAim(false);
            yield return Run(0.5f);
            Assert.Greater(Camera.main.fieldOfView, 70f);
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

        /// <summary>Der zweite Client sprintet in Blickrichtung <paramref name="yaw"/>, bis die Bedingung gilt.</summary>
        private IEnumerator WalkOther(float yaw, Func<bool> until, float seconds)
        {
            _otherInput = new PlayerInput { Buttons = InputButtons.Forward | InputButtons.Sprint, Yaw = yaw };
            var timeout = Time.realtimeSinceStartup + seconds;
            while (!until() && Time.realtimeSinceStartup < timeout) yield return Run(0.05f);
            _otherInput = default;
            yield return Run(0.4f);
        }

        /// <summary>
        /// Blau (Unity) läuft aus dem Spawn im Süden nach +z, Grün (zweiter Client) aus dem Spawn im Norden nach −z, beide bei
        /// x = 53,5 (erster Spawnpunkt jedes Teams), bis sie sich auf 14 Blöcke nah sind. Beide stehen dann in der Kampfzone.
        /// </summary>
        private IEnumerator MeetInTheMiddle()
        {
            Func<bool> close = () => Distance(_game.Player.State.Position, _other.Prediction.State.Position) < 14;
            _game.Player.TestLook(0, 0);
            _game.Player.TestButtons = InputButtons.Forward | InputButtons.Sprint;
            yield return WalkOther(180, close, 20);
            _game.Player.TestButtons = null;
            yield return Run(0.5f);
            Assert.IsTrue(close(), $"nah genug: {_game.Player.State.Position} / {_other.Prediction.State.Position}");
            var combat = _game.Metadata.CombatZone.Value;
            foreach (var p in new[] { _game.Player.State.Position, _other.Prediction.State.Position })
                Assert.IsTrue(combat.Contains(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y), Mathf.FloorToInt(p.Z)), $"{p} in der Kampfzone");
        }

        /// <summary>Wartet, bis der zweite Client im Team ist, gespawnt hat und von der Spielszene gezeigt wird.</summary>
        private IEnumerator WaitForOther()
        {
            var timeout = Time.realtimeSinceStartup + 10f;
            while ((_other.Team != _otherTeam || !_other.Alive || _game.RemotePlayers.Count == 0) && Time.realtimeSinceStartup < timeout)
            {
                UpdateOther();
                yield return null;
            }
            Assert.AreEqual(ClientPhase.Playing, _other.Phase, _other.Error);
            Assert.AreEqual(_otherTeam, _other.Team);
            Assert.IsTrue(_other.Alive, "zweiter Client gespawnt");
            yield return Run(0.3f);
        }

        /// <param name="cls">Klasse, die der zweite Client vor dem Spawn wählt (<c>null</c> = keine, also die Standardklasse).</param>
        private void ConnectOtherClient(Team team, string cls = null)
        {
            _otherTeam = team;
            _otherClass = cls;
            _otherTeamRequested = false;
            _otherInput = default;
            var registry = _game.Registry;
            _otherNet = new NetClient();
            _other = new ClientSession(registry, _game.Rules, File.ReadAllText(GameDataPaths.DataFile("player_rig.json")),
                name => File.ReadAllBytes(Path.Combine(GameDataPaths.MapsFolder, name + MapFile.Extension)),
                _otherNet, () => Time.realtimeSinceStartupAsDouble);
            _otherNet.Session = _other;
            ServerAddress.TryParse("127.0.0.1:" + _port, out var address);
            _otherNet.Connect(address, DevJoinTicket.Create(Guid.NewGuid(), "Zweiter", DateTimeOffset.UtcNow));
            _otherNextTick = Time.realtimeSinceStartupAsDouble;
        }

        private void UpdateOther()
        {
            _otherNet.Poll();
            _other.Update();
            if (_other.Phase == ClientPhase.Playing && !_otherTeamRequested)
            {
                if (_otherClass != null) _other.RequestClass(_other.Classes.Find(_otherClass));
                _other.RequestTeam(_otherTeam);
                _otherTeamRequested = true;
            }
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

        internal static string FindServer()
        {
            foreach (var config in new[] { "debug", "release" })
            {
                var path = Path.Combine(GameDataPaths.RepositoryRoot, "artifacts", "bin", "AoS2.Server", config, "AoS2.Server.dll");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        internal static int FreeUdpPort()
        {
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }
    }
}
