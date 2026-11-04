using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;

namespace AoS2.Server;

/// <summary>
/// Alles, was der Server vor dem Start lädt: Blocktypen, Spielregeln (Bewegung, Kampf, Waffen, Klassen), das Spielermodell
/// (Trefferboxen und Animationen) und die Map samt Prüfsumme.
/// </summary>
public sealed class GameContent
{
    public GameContent(BlockRegistry registry, GameRules rules, string playerRigJson, string mapName, byte[] mapFile)
    {
        var rig = PlayerRig.Parse(playerRigJson);
        if (rig.Fps != rules.Movement.TickRate)
            throw new FormatException($"player_rig.json ist mit {rig.Fps} Bildern/s abgetastet, der Server rechnet mit {rules.Movement.TickRate} Ticks/s.");
        Registry = registry;
        Rules = rules;
        RigJson = playerRigJson;
        Rig = rig;
        MapName = mapName;
        MapFileBytes = mapFile;
        MapChecksum = Shared.Net.MapChecksum.Compute(mapFile);
    }

    public BlockRegistry Registry { get; }

    /// <summary>Spielregeln aus data/. Die Texte gehen beim Beitritt an jeden Client (<see cref="MessageType.GameData"/>).</summary>
    public GameRules Rules { get; }

    /// <summary>Bewegung ohne Klasse (Tickrate, Schwerkraft, Zuschauer). Spieler rechnen mit <see cref="PlayerClass.Movement"/>.</summary>
    public MovementSettings Movement => Rules.Movement;

    public CombatSettings Combat => Rules.Combat;
    public PlayerClasses Classes => Rules.Classes;
    public PlayerRig Rig { get; }

    /// <summary>player_rig.json als Text (Clients im selben Prozess, z. B. Lasttest-Bots, lesen damit die Regeln des Servers).</summary>
    public string RigJson { get; }

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
        var rules = GameRules.Parse(GameDataFiles.Load(Data), rigJson);
        var mapPath = Path.Combine(options.MapsFolder, options.Map + MapFile.Extension);
        if (!File.Exists(mapPath)) throw new FileNotFoundException($"Map nicht gefunden: {mapPath}");
        var content = new GameContent(registry, rules, rigJson, options.Map, File.ReadAllBytes(mapPath));
        content.LoadMap(); // kaputte Map sofort melden, nicht erst beim ersten Spieler
        return content;
    }
}
