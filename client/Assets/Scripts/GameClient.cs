using System;
using System.Collections;
using System.IO;
using AoS2.Client.Account;
using AoS2.Client.Player;
using AoS2.Client.World;
using AoS2.Net;
using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client
{
    public enum GameMode
    {
        /// <summary>Startmenü: noch kein Spiel geladen.</summary>
        Menu,

        /// <summary>Ohne Server wie in M3: Der Client rechnet alles selbst.</summary>
        Offline,

        /// <summary>Mit einem Spielserver verbunden (oder beim Verbinden).</summary>
        Online,
    }

    /// <summary>
    /// Das Spiel auf diesem Rechner: lädt Blocktypen und Bewegungswerte, baut die Darstellung der Welt auf und
    /// rechnet in festen Schritten (60 Ticks/s wie der Server).
    /// <para>
    /// <b>Offline</b> rechnet der Client alles selbst (Bewegung, Blöcke, fallende Gruppen). <b>Online</b> schickt er Eingaben
    /// und Blockabsichten an den Server, sagt die eigene Bewegung voraus (<see cref="ClientSession"/>) und zeigt andere
    /// Spieler und fallende Blöcke so, wie der Server sie meldet.
    /// </para>
    /// Warum feste Schritte? Die Bildrate schwankt, die Simulation darf das nicht: Nur mit gleich langen Ticks kommt
    /// bei gleicher Eingabe überall dasselbe heraus. Zwischen zwei Ticks wird für die Anzeige interpoliert.
    /// </summary>
    public sealed class GameClient : MonoBehaviour
    {
        [SerializeField] private Shader voxelShader;
        [SerializeField] private Shader lineShader;
        [SerializeField] private string mapName = "testmap";

        private BlockTextures _textures;
        private Material _voxelMaterial;
        private Material _lineMaterial;
        private VoxelMeshBuilder _builder;
        private FallingBlocks _offlineFalling;
        private float _accumulator;
        private bool _atmosphereSet;

        public static GameClient Instance { get; private set; }

        /// <summary>Für Tests: ohne Startmenü sofort offline spielen.</summary>
        public static bool StartOfflineImmediately { get; set; }

        public GameMode Mode { get; private set; } = GameMode.Menu;

        public BlockRegistry Registry { get; private set; }
        public MovementSettings Movement { get; private set; }
        public VoxelWorld World { get; private set; }
        public MapMetadata Metadata { get; private set; }

        /// <summary>Fallende Gruppen: offline selbst gerechnet, online vom Server.</summary>
        public IFallingGroups Falling { get; private set; }

        public WorldRenderer WorldRenderer { get; private set; }
        public FallingBlocksView FallingView { get; private set; }
        public RemotePlayersView RemotePlayers { get; private set; }
        public LocalPlayer Player { get; private set; }

        /// <summary>Netzspiel (nur online, sonst <c>null</c>).</summary>
        public ClientSession Session { get; private set; }
        public NetClient Net { get; private set; }

        /// <summary>Adresse des Servers, wie eingegeben (für die Anzeige).</summary>
        public string ServerAddressText { get; private set; }

        /// <summary>Zahl der bisher gerechneten Ticks.</summary>
        public long Tick { get; private set; }

        /// <summary>Welt geladen, Spieler steht: Es wird gespielt.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Fehler beim Laden der Spieldaten (dann geht gar nichts), sonst <c>null</c>.</summary>
        public string LoadError { get; private set; }

        /// <summary>Was gerade passiert („Verbinde …“) oder warum die letzte Verbindung endete (für das Startmenü).</summary>
        public string Status { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            try
            {
                LoadData();
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                LoadError = e.Message;
                Debug.LogException(e);
                return;
            }

            var connect = CommandLine.Value("-connect");
            if (connect != null) Connect(connect, CommandLine.Value("-name") ?? "Spieler", !PlayerAccount.IsSignedIn || CommandLine.Has("-devticket"));
            else if (StartOfflineImmediately || CommandLine.Has("-offline")) StartOffline();
        }

        /// <summary>Blocktypen, Bewegungswerte und Texturen (einmal beim Start, gilt für beide Spielarten).</summary>
        private void LoadData()
        {
            Registry = BlockRegistry.Parse(File.ReadAllText(GameDataPaths.DataFile("blocks.json")));
            Movement = MovementSettings.Parse(
                File.ReadAllText(GameDataPaths.DataFile("movement.json")),
                File.ReadAllText(GameDataPaths.DataFile("player_rig.json")));
            _textures = BlockTextures.Load(Registry, GameDataPaths.BlockTexturesFolder);
            _voxelMaterial = new Material(voxelShader) { name = "Voxel" };
            _voxelMaterial.SetTexture("_BlockTextures", _textures.Array);
            _lineMaterial = new Material(lineShader) { name = "Blockrahmen" };
            _builder = new VoxelMeshBuilder(_textures.FaceTable);
        }

        // ---- Offline ----

        public void StartOffline()
        {
            if (LoadError != null || Mode != GameMode.Menu) return;
            try
            {
                var map = MapFile.Load(MapPath(mapName), Registry);
                World = map.World;
                Metadata = map.Metadata;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Status = "Map konnte nicht geladen werden: " + e.Message;
                Debug.LogException(e);
                return;
            }
            _offlineFalling = new FallingBlocks(World, Movement.Gravity, Movement.MaxFallSpeed);
            Falling = _offlineFalling;
            Mode = GameMode.Offline;
            BuildWorldView(FindSpawn());
            IsReady = true;
            Status = null;
        }

        // ---- Online ----

        /// <param name="useDevTicket">Ein Entwicklungs-Ticket selbst ausstellen (nur Server mit <c>--dev</c> nehmen es an)
        /// statt ein echtes beim Backend zu holen.</param>
        public void Connect(string address, string playerName, bool useDevTicket)
        {
            if (LoadError != null || Mode != GameMode.Menu) return;
            if (!ServerAddress.TryParse(address, out var parsed))
            {
                Status = $"\"{address}\" ist keine gültige Server-Adresse (z. B. 127.0.0.1:{ProtocolInfo.DefaultPort}).";
                return;
            }
            StartCoroutine(ConnectRoutine(parsed, playerName, useDevTicket));
        }

        private IEnumerator ConnectRoutine(ServerAddress address, string playerName, bool useDevTicket)
        {
            Mode = GameMode.Online;
            ServerAddressText = address.ToString();
            // Ohne Fokus hält Unity sonst an: Netzwerk und Anzeige der anderen Spieler würden stehen bleiben
            // (z. B. beim Test mit Editor und Build auf einem Rechner).
            Application.runInBackground = true;
            string ticket = null;
            if (useDevTicket)
            {
                ticket = DevJoinTicket.Create(Guid.NewGuid(), string.IsNullOrWhiteSpace(playerName) ? "Spieler" : playerName.Trim(), DateTimeOffset.UtcNow);
            }
            else
            {
                Status = "Hole Join-Ticket …";
                string error = null;
                yield return JoinTicketClient.Request(address.ToString(), t => ticket = t, e => error = e);
                if (ticket == null)
                {
                    Mode = GameMode.Menu;
                    Status = error;
                    yield break;
                }
            }

            Status = $"Verbinde mit {address} …";
            Net = new NetClient();
            Session = new ClientSession(Registry, Movement, ReadMapFile, Net, () => Time.realtimeSinceStartupAsDouble);
            Session.EditRejected += result => Player?.ShowEditResult(result);
            Net.Session = Session;
            Net.Connect(address, ticket);
        }

        private byte[] ReadMapFile(string name)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) return null;
            var path = MapPath(name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private void UpdateNetwork()
        {
            Net.Poll();
            if (Session.Phase == ClientPhase.Disconnected)
            {
                var reason = Session.Error ?? Net.DisconnectMessage ?? "Verbindung getrennt.";
                LeaveToMenu();
                Status = reason;
                return;
            }
            if (Session.World != null && WorldRenderer == null)
            {
                World = Session.World;
                Metadata = Session.Metadata;
                Falling = Session.Falling;
                BuildWorldView(Session.Prediction.State.Position);
                Status = "Lade Welt …";
            }
            if (!IsReady && Session.Phase == ClientPhase.Playing)
            {
                IsReady = true;
                Status = null;
                Debug.Log($"[AoS2] Verbunden mit {ServerAddressText} als {Session.PlayerName}, Map \"{Session.MapName}\"");
            }
        }

        // ---- Ablauf ----

        private void Update()
        {
            if (Mode == GameMode.Online && Session != null)
            {
                UpdateNetwork();
                if (Mode != GameMode.Online) return;
            }
            if (!IsReady) return;

            Player.ReadFrameInput();
            if (!IsReady) return; // Esc hat das Spiel beendet

            // Feste Ticks nachholen; nach einem Hänger höchstens eine Viertelsekunde, sonst läuft die Simulation davon.
            var dt = Movement.DeltaTime;
            _accumulator = Mathf.Min(_accumulator + Time.deltaTime, 0.25f);
            while (_accumulator >= dt)
            {
                if (Mode == GameMode.Offline) FallingView.BeforeTick();
                Player.Tick();
                if (Mode == GameMode.Offline) _offlineFalling.Tick(dt);
                Tick++;
                _accumulator -= dt;
            }

            var alpha = _accumulator / dt;
            if (Mode == GameMode.Online)
            {
                Session.Update();
                Player.ApplyNetworkCorrection(Session.TakeCorrection());
                FallingView.UpdateVisuals(1); // die Session hat die Fallhöhe schon interpoliert
                RemotePlayers.Sync(Session);
            }
            else
            {
                FallingView.UpdateVisuals(alpha);
            }
            Player.UpdateVisuals(alpha);
        }

        /// <summary>Zurück ins Startmenü: Verbindung trennen, Welt abbauen.</summary>
        public void LeaveToMenu()
        {
            StopAllCoroutines();
            if (Net != null)
            {
                Net.Session = null;
                Net.Disconnect();
                Net.Poll();
                Net.Dispose();
            }
            Net = null;
            Session = null;
            IsReady = false;
            DestroyWorldView();
            World = null;
            Metadata = null;
            Falling = null;
            _offlineFalling = null;
            Mode = GameMode.Menu;
            Status = null;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void BuildWorldView(Vec3 spawn)
        {
            var started = Time.realtimeSinceStartup;
            WorldRenderer = new GameObject("Welt").AddComponent<WorldRenderer>();
            WorldRenderer.Initialize(World, _builder, _voxelMaterial);
            FallingView = new GameObject("Fallende Blöcke").AddComponent<FallingBlocksView>();
            FallingView.Initialize(Falling, _builder, _voxelMaterial);
            if (Mode == GameMode.Online)
            {
                RemotePlayers = new GameObject("Andere Spieler").AddComponent<RemotePlayersView>();
                RemotePlayers.Initialize(Registry, Movement, _builder, _voxelMaterial);
            }

            var camera = Camera.main;
            if (camera == null) camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" }.GetComponent<Camera>();
            Player = camera.gameObject.AddComponent<LocalPlayer>();
            Player.Initialize(this, spawn, _lineMaterial);

            if (!_atmosphereSet) SetupAtmosphere(camera);
            _atmosphereSet = true;
            _accumulator = 0;
            Debug.Log($"[AoS2] Map \"{Metadata.Name}\" geladen: {World.SizeX} × {World.SizeY} × {World.SizeZ}, " +
                      $"{WorldRenderer.ChunkCount} Chunks in {(Time.realtimeSinceStartup - started) * 1000:0} ms");
        }

        private void DestroyWorldView()
        {
            if (Player != null) Destroy(Player);
            if (WorldRenderer != null) Destroy(WorldRenderer.gameObject);
            if (FallingView != null) Destroy(FallingView.gameObject);
            if (RemotePlayers != null) Destroy(RemotePlayers.gameObject);
            Player = null;
            WorldRenderer = null;
            FallingView = null;
            RemotePlayers = null;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Net?.Dispose();
            _textures?.Dispose();
            if (_voxelMaterial != null) Destroy(_voxelMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        private void OnApplicationQuit() => Net?.Disconnect();

        // ---- Abbauen und Bauen: offline sofort, online als Anfrage an den Server (sofort angezeigt) ----

        public BlockEditResult TryRemoveBlock(int x, int y, int z, float distance)
        {
            if (Mode == GameMode.Online) return Session.RequestRemove(x, y, z, distance);
            var result = BlockEditing.CanRemove(World, Registry, x, y, z, distance);
            if (result != BlockEditResult.Ok) return result;
            World.SetBlock(x, y, z, BlockRegistry.Air);
            _offlineFalling.CheckAfterRemoval(x, y, z);
            return result;
        }

        public BlockEditResult TryPlaceBlock(int x, int y, int z, byte id, float distance, PlayerState player)
        {
            if (Mode == GameMode.Online) return Session.RequestPlace(x, y, z, id, distance);
            var result = BlockEditing.CanPlace(World, Registry, x, y, z, id, distance, player, Movement);
            if (result == BlockEditResult.Ok) World.SetBlock(x, y, z, id);
            return result;
        }

        /// <summary>Erster Spawnpunkt; ohne Metadaten die Mitte der Map auf dem höchsten Block.</summary>
        public Vec3 FindSpawn() => Spawns.Pick(World, Metadata, 0);

        private static string MapPath(string name) => Path.Combine(GameDataPaths.MapsFolder, name + MapFile.Extension);

        private static void SetupAtmosphere(Camera camera)
        {
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 400f;
            camera.fieldOfView = 75f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 180f;
        }
    }

    /// <summary>Argumente beim Start des Spiels, z. B. <c>AoS2.exe -connect 127.0.0.1:32887 -name Jan</c>.</summary>
    public static class CommandLine
    {
        public static bool Has(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        public static string Value(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
