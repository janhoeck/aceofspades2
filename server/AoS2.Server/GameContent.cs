using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;

namespace AoS2.Server;

/// <summary>
/// Alles, was der Server vor dem Start lädt: Blocktypen, Bewegungswerte, Kampfregeln und Waffen, das Spielermodell
/// (Trefferboxen und Animationen) und die Map samt Prüfsumme.
/// </summary>
public sealed class GameContent
{
    public GameContent(BlockRegistry registry, MovementSettings movement, CombatSettings combat, PlayerRig rig, string mapName, byte[] mapFile)
    {
        if (rig.Fps != movement.TickRate)
            throw new FormatException($"player_rig.json ist mit {rig.Fps} Bildern/s abgetastet, der Server rechnet mit {movement.TickRate} Ticks/s.");
        Registry = registry;
        Movement = movement;
        Combat = combat;
        Rig = rig;
        MapName = mapName;
        MapFileBytes = mapFile;
        MapChecksum = Shared.Net.MapChecksum.Compute(mapFile);
    }

    public BlockRegistry Registry { get; }
    public MovementSettings Movement { get; }
    public CombatSettings Combat { get; }
    public PlayerRig Rig { get; }
    public string MapName { get; }

    /// <summary>Die Map-Datei, wie sie auf der Platte liegt. Jedes Match lädt daraus eine frische Welt.</summary>
    public byte[] MapFileBytes { get; }

    public byte[] MapChecksum { get; }

    public GameMap LoadMap() => MapFile.Read(new MemoryStream(MapFileBytes, writable: false), Registry);

    public static GameContent Load(ServerOptions options)
    {
        string Data(string name) => File.ReadAllText(Path.Combine(options.DataFolder, name));
        var registry = BlockRegistry.Parse(Data("blocks.json"));
        var rigJson = Data("player_rig.json");
        var movement = MovementSettings.Parse(Data("movement.json"), rigJson);
        var combat = CombatSettings.Parse(Data("combat.json"), Data("weapons.json"), movement.TickRate);
        var rig = PlayerRig.Parse(rigJson);
        var mapPath = Path.Combine(options.MapsFolder, options.Map + MapFile.Extension);
        if (!File.Exists(mapPath)) throw new FileNotFoundException($"Map nicht gefunden: {mapPath}");
        var content = new GameContent(registry, movement, combat, rig, options.Map, File.ReadAllBytes(mapPath));
        content.LoadMap(); // kaputte Map sofort melden, nicht erst beim ersten Spieler
        return content;
    }
}
