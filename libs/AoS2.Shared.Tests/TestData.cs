using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

/// <summary>Hilfen für Tests: kleine Registry, flache Welten, Pfade zu den echten Dateien in data/.</summary>
internal static class TestData
{
    public const byte Bedrock = 1;
    public const byte Stone = 2;
    public const byte Dirt = 3;
    public const byte Build = 9;

    public static BlockRegistry Registry { get; } = BlockRegistry.Parse("""
        { "blocks": [
          { "id": 1, "name": "bedrock", "indestructible": true, "textures": { "all": "b" } },
          { "id": 2, "name": "stone", "textures": { "all": "s" } },
          { "id": 3, "name": "dirt", "textures": { "all": "d" } },
          { "id": 9, "name": "build_blue", "buildable": true, "textures": { "all": "x" } }
        ] }
        """);

    /// <summary>Eine Klasse mit diesen Waffen (Plätze 1, 2 …) und den Standard-Bewegungswerten, für Tests der Waffen-Simulation.</summary>
    public static PlayerClass Loadout(CombatSettings combat, params string[] weapons) =>
        new("test", new MovementSettings(), combat, weapons.Select(name => combat.Find(name) ?? throw new ArgumentException(name)));

    /// <summary>Text einer Datei aus data/.</summary>
    public static string DataFile(string name) => File.ReadAllText(RepoFile("data/" + name));

    /// <summary>Spielregeln aus dem echten data/-Ordner (wie Server und Client sie laden).</summary>
    public static GameRules RealRules() => GameRules.Parse(GameDataFiles.Load(DataFile), DataFile("player_rig.json"));

    /// <summary>Welt mit Grundgestein auf y = 0 und Stein bis einschließlich <paramref name="groundTop"/>.</summary>
    public static VoxelWorld FlatWorld(int sizeX = 32, int sizeY = 32, int sizeZ = 32, int groundTop = 3)
    {
        var world = new VoxelWorld(sizeX, sizeY, sizeZ);
        for (var x = 0; x < sizeX; x++)
            for (var z = 0; z < sizeZ; z++)
                for (var y = 0; y <= groundTop; y++)
                    world.SetBlock(x, y, z, y == 0 ? Bedrock : Stone);
        return world;
    }

    /// <summary>Pfad einer Datei relativ zum Repository (sucht AoS2.slnx von der Testausgabe aufwärts).</summary>
    public static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AoS2.slnx"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Repository-Wurzel (AoS2.slnx) nicht gefunden.");
        return Path.Combine(dir.FullName, relativePath);
    }
}
