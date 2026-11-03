using System;
using System.IO;
using AoS2.Client.Player;
using AoS2.Client.World;
using AoS2.Shared;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;
using UnityEngine;

namespace AoS2.Client
{
    /// <summary>
    /// Lokales Spiel ohne Server (M3): lädt Blocktypen, Bewegungswerte und Map, baut die Darstellung auf
    /// und rechnet die Simulation in festen Schritten (60 Ticks/s wie später der Server).
    ///
    /// Warum feste Schritte? Die Bildrate schwankt, die Simulation darf das nicht: Nur mit gleich langen
    /// Ticks kommt bei gleicher Eingabe überall dasselbe heraus. Zwischen zwei Ticks wird für die Anzeige
    /// interpoliert, damit es bei 144 Hz trotzdem flüssig aussieht.
    /// </summary>
    public sealed class LocalGame : MonoBehaviour
    {
        [SerializeField] private Shader voxelShader;
        [SerializeField] private Shader lineShader;
        [SerializeField] private string mapName = "testmap";

        private BlockTextures _textures;
        private Material _voxelMaterial;
        private FallingBlocksView _fallingView;
        private float _accumulator;

        public static LocalGame Instance { get; private set; }

        public BlockRegistry Registry { get; private set; }
        public MovementSettings Movement { get; private set; }
        public VoxelWorld World { get; private set; }
        public MapMetadata Metadata { get; private set; }
        public FallingBlocks Falling { get; private set; }
        public WorldRenderer WorldRenderer { get; private set; }
        public FallingBlocksView FallingView => _fallingView;
        public LocalPlayer Player { get; private set; }

        /// <summary>Zahl der bisher gerechneten Ticks.</summary>
        public long Tick { get; private set; }

        public bool IsReady { get; private set; }

        /// <summary>Fehlermeldung beim Laden (wird im HUD angezeigt), sonst <c>null</c>.</summary>
        public string LoadError { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            try
            {
                Load();
                IsReady = true;
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                LoadError = e.Message;
                Debug.LogException(e);
            }
        }

        private void Load()
        {
            var started = Time.realtimeSinceStartup;
            Registry = BlockRegistry.Parse(File.ReadAllText(GameDataPaths.DataFile("blocks.json")));
            Movement = MovementSettings.Parse(
                File.ReadAllText(GameDataPaths.DataFile("movement.json")),
                File.ReadAllText(GameDataPaths.DataFile("player_rig.json")));

            var map = MapFile.Load(Path.Combine(GameDataPaths.MapsFolder, mapName + MapFile.Extension), Registry);
            World = map.World;
            Metadata = map.Metadata;
            Falling = new FallingBlocks(World, Movement.Gravity, Movement.MaxFallSpeed);

            _textures = BlockTextures.Load(Registry, GameDataPaths.BlockTexturesFolder);
            _voxelMaterial = new Material(voxelShader) { name = "Voxel" };
            _voxelMaterial.SetTexture("_BlockTextures", _textures.Array);
            var builder = new VoxelMeshBuilder(_textures.FaceTable);

            WorldRenderer = new GameObject("Welt").AddComponent<WorldRenderer>();
            WorldRenderer.Initialize(World, builder, _voxelMaterial);
            _fallingView = new GameObject("Fallende Blöcke").AddComponent<FallingBlocksView>();
            _fallingView.Initialize(Falling, builder, _voxelMaterial);

            var camera = Camera.main;
            if (camera == null) camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" }.GetComponent<Camera>();
            Player = camera.gameObject.AddComponent<LocalPlayer>();
            Player.Initialize(this, FindSpawn(), new Material(lineShader) { name = "Blockrahmen" });

            SetupAtmosphere(camera);
            Debug.Log($"[AoS2] Map \"{Metadata.Name}\" geladen: {World.SizeX} × {World.SizeY} × {World.SizeZ}, " +
                      $"{WorldRenderer.ChunkCount} Chunks in {(Time.realtimeSinceStartup - started) * 1000:0} ms");
        }

        private void Update()
        {
            if (!IsReady) return;
            Player.ReadFrameInput();

            // Feste Ticks nachholen; nach einem Hänger höchstens eine Viertelsekunde, sonst läuft die Simulation davon.
            var dt = Movement.DeltaTime;
            _accumulator = Mathf.Min(_accumulator + Time.deltaTime, 0.25f);
            while (_accumulator >= dt)
            {
                _fallingView.BeforeTick();
                Player.Tick();
                Falling.Tick(dt);
                Tick++;
                _accumulator -= dt;
            }

            var alpha = _accumulator / dt;
            Player.UpdateVisuals(alpha);
            _fallingView.UpdateVisuals(alpha);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _textures?.Dispose();
            if (_voxelMaterial != null) Destroy(_voxelMaterial);
        }

        // ---- Abbauen und Bauen (heute lokal, ab M4 als Anfrage an den Server) ----

        public BlockEditResult TryRemoveBlock(int x, int y, int z, float distance)
        {
            var result = BlockEditing.CanRemove(World, Registry, x, y, z, distance);
            if (result != BlockEditResult.Ok) return result;
            World.SetBlock(x, y, z, BlockRegistry.Air);
            Falling.CheckAfterRemoval(x, y, z);
            return result;
        }

        public BlockEditResult TryPlaceBlock(int x, int y, int z, byte id, float distance, PlayerState player)
        {
            var result = BlockEditing.CanPlace(World, Registry, x, y, z, id, distance, player, Movement);
            if (result == BlockEditResult.Ok) World.SetBlock(x, y, z, id);
            return result;
        }

        /// <summary>Erster Spawnpunkt von Blau; ohne Metadaten die Mitte der Map auf dem höchsten Block.</summary>
        public Vec3 FindSpawn()
        {
            foreach (var point in Metadata.AllSpawnPoints()) return point;
            int x = World.SizeX / 2, z = World.SizeZ / 2, y = World.SizeY;
            while (y > 0 && !World.IsSolid(x, y - 1, z)) y--;
            return new Vec3(x + 0.5f, y, z + 0.5f);
        }

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
}
