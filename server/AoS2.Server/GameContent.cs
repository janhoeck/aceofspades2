using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Voxels;

namespace AoS2.Server;

/// <summary>Alles, was der Server vor dem Start lädt: Blocktypen, Bewegungswerte und die Map samt Prüfsumme.</summary>
public sealed class GameContent
{
    public GameContent(BlockRegistry registry, MovementSettings movement, string mapName, byte[] mapFile)
    {
        Registry = registry;
        Movement = movement;
        MapName = mapName;
        MapFileBytes = mapFile;
        MapChecksum = Shared.Net.MapChecksum.Compute(mapFile);
    }

    public BlockRegistry Registry { get; }
    public MovementSettings Movement { get; }
    public string MapName { get; }

    /// <summary>Die Map-Datei, wie sie auf der Platte liegt. Jedes Match lädt daraus eine frische Welt.</summary>
    public byte[] MapFileBytes { get; }

    public byte[] MapChecksum { get; }

    public GameMap LoadMap() => MapFile.Read(new MemoryStream(MapFileBytes, writable: false), Registry);

    public static GameContent Load(ServerOptions options)
    {
        var registry = BlockRegistry.Parse(File.ReadAllText(Path.Combine(options.DataFolder, "blocks.json")));
        var movement = MovementSettings.Parse(
            File.ReadAllText(Path.Combine(options.DataFolder, "movement.json")),
            File.ReadAllText(Path.Combine(options.DataFolder, "player_rig.json")));
        var mapPath = Path.Combine(options.MapsFolder, options.Map + MapFile.Extension);
        if (!File.Exists(mapPath)) throw new FileNotFoundException($"Map nicht gefunden: {mapPath}");
        var content = new GameContent(registry, movement, options.Map, File.ReadAllBytes(mapPath));
        content.LoadMap(); // kaputte Map sofort melden, nicht erst beim ersten Spieler
        return content;
    }
}
