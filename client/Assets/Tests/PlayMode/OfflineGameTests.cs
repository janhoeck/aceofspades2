using System.Collections;
using System.IO;
using AoS2.Shared;
using AoS2.Shared.Voxels;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AoS2.Client.Tests
{
    /// <summary>
    /// Startet die echte Spielszene mit der Testmap: Welt wird angezeigt, der Spieler landet auf dem Spawn,
    /// Abbauen lässt eine Plattform fallen, Bauen setzt einen Block. Legt Screenshots in artifacts/screenshots ab.
    /// </summary>
    public class OfflineGameTests
    {
        private GameClient _game;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            GameClient.StartOfflineImmediately = true;
            // Erst warten, bis die Szene gewechselt hat; sonst ist Instance noch der GameClient des vorigen Tests.
            var loading = SceneManager.LoadSceneAsync("Game");
            while (!loading.isDone) yield return null;
            var timeout = Time.realtimeSinceStartup + 20f;
            while ((GameClient.Instance == null || (!GameClient.Instance.IsReady && GameClient.Instance.LoadError == null)) && Time.realtimeSinceStartup < timeout)
                yield return null;
            _game = GameClient.Instance;
            Assert.IsNotNull(_game, "GameClient fehlt in der Szene");
            Assert.IsNull(_game.LoadError);
            Assert.IsTrue(_game.IsReady);
        }

        [UnityTest]
        public IEnumerator World_IsRendered_PlayerLandsOnSpawn()
        {
            Assert.AreEqual(4 * 2 * 4, _game.WorldRenderer.ChunkCount); // 128 × 64 × 128 in Chunks à 32³
            yield return WaitTicks(90);

            var state = _game.Player.State;
            Assert.IsTrue(state.OnGround, "Spieler steht");
            Assert.AreEqual(15f, state.Position.Y, 1e-4, "auf der Spawn-Plattform (Oberkante y = 15)");

            yield return Screenshot("m3-spawn", new Vec3(64.5f, 15, 14.5f), yaw: -25, pitch: 12);
            yield return Screenshot("m3-overview", new Vec3(8, 40, 8), yaw: 45, pitch: 30);
        }

        [UnityTest]
        public IEnumerator RemovingPillar_LetsPlatformFall()
        {
            // T-Form der Testmap: Steinsäule bei (40, z = 50) mit 7 × 7 Plattform aus Baublöcken obendrauf
            var world = _game.World;
            const int x = 40, z = 50;
            var build = _game.Registry.Get("build_blue").Id;
            var top = world.SizeY - 1;
            while (top > 0 && world.GetBlock(x, top, z) != build) top--;
            Assert.Greater(top, 0, "Plattform der T-Form nicht gefunden");

            yield return Screenshot("m3-falling-before", new Vec3(x + 0.5f, top - 6, z - 9.5f), yaw: 0, pitch: -20);

            var cut = top - 3;
            Assert.AreEqual(BlockEditResult.Ok, _game.TryRemoveBlock(x, cut, z, 1f));
            Assert.AreEqual(1, _game.Falling.Groups.Count, "Plattform samt Säulenrest darüber fällt");
            Assert.IsFalse(world.IsSolid(x, top, z), "fallende Blöcke stehen nicht mehr in der Welt");

            yield return null;
            Assert.AreEqual(1, _game.FallingView.ActiveCount, "fallende Gruppe wird angezeigt");

            yield return WaitTicks(120);
            Assert.AreEqual(0, _game.Falling.Groups.Count, "gelandet");
            Assert.AreEqual(0, _game.FallingView.ActiveCount);
            // Unterster Gruppenblock war cut + 1 und landet auf dem Säulenstumpf (cut - 1): eine Reihe tiefer
            Assert.AreEqual(build, world.GetBlock(x - 3, top - 1, z - 3));
            Assert.IsFalse(world.IsSolid(x - 3, top, z - 3));

            yield return Screenshot("m3-falling-after", new Vec3(x + 0.5f, top - 6, z - 9.5f), yaw: 0, pitch: -20);
        }

        [UnityTest]
        public IEnumerator Building_PlacesBlock_IndestructibleStays()
        {
            var spawn = _game.FindSpawn();
            int x = (int)spawn.X + 2, y = (int)spawn.Y, z = (int)spawn.Z;
            var build = _game.Registry.Get("build_blue").Id;

            Assert.AreEqual(BlockEditResult.Ok, _game.TryPlaceBlock(x, y, z, build, 2f, _game.Player.State));
            Assert.AreEqual(build, _game.World.GetBlock(x, y, z));
            Assert.AreEqual(BlockEditResult.Indestructible, _game.TryRemoveBlock(x, y - 1, z, 2f), "Spawn-Block ist unzerstörbar");
            yield return WaitTicks(30);
            Assert.AreEqual(build, _game.World.GetBlock(x, y, z), "gebauter Block hängt am Boden und fällt nicht");
        }

        [UnityTest]
        public IEnumerator SteppingUp_CameraNeverDips()
        {
            // Stufe aus Baublöcken (1 hoch, 3 tief) vor dem Spawn, der Spieler läuft nach +Z darauf zu.
            var spawn = _game.FindSpawn();
            var build = _game.Registry.Get("build_blue").Id;
            int y = (int)spawn.Y, z0 = (int)spawn.Z + 2;
            for (var x = (int)spawn.X - 2; x <= (int)spawn.X + 2; x++)
                for (var z = z0; z < z0 + 3; z++)
                    _game.World.SetBlock(x, y, z, build);
            _game.Player.Teleport(spawn, yaw: 0, pitch: 0);
            yield return WaitTicks(5);

            var camera = Camera.main.transform;
            var last = camera.position.y;
            var lowest = 0f;
            _game.Player.TestButtons = AoS2.Shared.Movement.InputButtons.Forward;
            var end = _game.Tick + 60;
            while (_game.Tick < end)
            {
                yield return null;
                lowest = Mathf.Min(lowest, camera.position.y - last);
                last = camera.position.y;
            }
            _game.Player.TestButtons = null;

            Assert.AreEqual(spawn.Y + 1, _game.Player.State.Position.Y, 1e-4, "Stufe hochgestiegen");
            Assert.GreaterOrEqual(lowest, -0.01f, "Kamera darf beim Hochsteigen nicht nach unten springen");
        }

        private IEnumerator WaitTicks(int ticks)
        {
            var target = _game.Tick + ticks;
            var timeout = Time.realtimeSinceStartup + 15f;
            while (_game.Tick < target && Time.realtimeSinceStartup < timeout) yield return null;
        }

        /// <summary>Kamera an eine Stelle setzen und ein Bild nach artifacts/screenshots schreiben.</summary>
        private IEnumerator Screenshot(string name, Vec3 feet, float yaw, float pitch)
        {
            _game.Player.Teleport(feet, yaw, pitch);
            yield return null;
            _game.Player.Teleport(feet, yaw, pitch); // nach dem Tick des Frames erneut, damit nichts verrutscht

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
            Object.Destroy(image);
            Object.Destroy(target);
        }
    }
}
