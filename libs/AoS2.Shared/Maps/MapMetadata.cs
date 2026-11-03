using System;
using System.Collections.Generic;
using AoS2.Shared.Json;

namespace AoS2.Shared.Maps
{
    /// <summary>Team eines Spielers. Der Zahlenwert steht so im Netzwerkprotokoll.</summary>
    public enum Team : byte
    {
        /// <summary>Noch kein Team gewählt (Zuschauer).</summary>
        None = 0,
        Blue = 1,
        Green = 2,
    }

    /// <summary>Ein achsenparalleler Quader aus ganzen Blöcken: <c>Min</c> gehört dazu, <c>Max</c> nicht.</summary>
    public struct BlockBox
    {
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public BlockBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public bool Contains(int x, int y, int z) =>
            x >= MinX && x < MaxX && y >= MinY && y < MaxY && z >= MinZ && z < MaxZ;
    }

    /// <summary>Spawnzone und Spawnpunkte eines Teams.</summary>
    public sealed class TeamSpawn
    {
        public BlockBox Zone;
        public List<Vec3> Points = new List<Vec3>();
    }

    /// <summary>
    /// JSON-Metadaten einer Map: Name, Kampfzone, Spawnzonen und Spawnpunkte. Beispiel:
    /// <code>
    /// { "name": "Testmap", "author": "…",
    ///   "combatZone": { "min": [0, 0, 16], "max": [128, 64, 112] },
    ///   "teams": { "blue": { "zone": { "min": [..], "max": [..] }, "spawnPoints": [[x, y, z], …] }, "green": { … } } }
    /// </code>
    /// Spawnpunkte sind Fußpositionen (Mitte unten) des Spielers.
    /// </summary>
    public sealed class MapMetadata
    {
        public string Name = "";
        public string Author = "";
        public BlockBox? CombatZone;
        public readonly Dictionary<Team, TeamSpawn> Teams = new Dictionary<Team, TeamSpawn>();

        /// <summary>Alle Spawnpunkte aller Teams (zum Testen ohne Teamauswahl).</summary>
        public IEnumerable<Vec3> AllSpawnPoints()
        {
            foreach (var team in Teams.Values)
                foreach (var p in team.Points)
                    yield return p;
        }

        public static MapMetadata Parse(string json)
        {
            var root = JsonParser.Parse(json);
            if (root.Kind != JsonKind.Object) throw new FormatException("Map-Metadaten müssen ein JSON-Objekt sein.");
            var meta = new MapMetadata
            {
                Name = root.GetString("name", ""),
                Author = root.GetString("author", ""),
            };
            var combat = root["combatZone"];
            if (combat != null && !combat.IsNull) meta.CombatZone = ReadBox(combat, "combatZone");

            var teams = root["teams"];
            if (teams != null && teams.Kind == JsonKind.Object)
            {
                foreach (var t in teams.Properties)
                {
                    var team = ParseTeam(t.Key);
                    var spawn = new TeamSpawn { Zone = ReadBox(t.Value["zone"], $"teams.{t.Key}.zone") };
                    var points = t.Value["spawnPoints"];
                    if (points != null)
                        foreach (var p in points.Items)
                            spawn.Points.Add(ReadVec(p, $"teams.{t.Key}.spawnPoints"));
                    meta.Teams[team] = spawn;
                }
            }
            return meta;
        }

        public string ToJson(bool indented = true)
        {
            var root = JsonValue.NewObject().Set("name", Name).Set("author", Author);
            if (CombatZone.HasValue) root.Set("combatZone", WriteBox(CombatZone.Value));
            var teams = JsonValue.NewObject();
            foreach (Team team in Enum.GetValues(typeof(Team)))
            {
                if (!Teams.TryGetValue(team, out var spawn)) continue;
                var points = JsonValue.NewArray();
                foreach (var p in spawn.Points)
                    points.Add(JsonValue.NewArray().Add(JsonValue.Number(p.X)).Add(JsonValue.Number(p.Y)).Add(JsonValue.Number(p.Z)));
                teams.Set(TeamName(team), JsonValue.NewObject().Set("zone", WriteBox(spawn.Zone)).Set("spawnPoints", points));
            }
            root.Set("teams", teams);
            return root.ToJson(indented);
        }

        public static string TeamName(Team team) => team == Team.Blue ? "blue" : team == Team.Green ? "green" : "none";

        /// <summary>Das andere Team (ohne Team: keins).</summary>
        public static Team Opponent(Team team) => team == Team.Blue ? Team.Green : team == Team.Green ? Team.Blue : Team.None;

        private static Team ParseTeam(string name)
        {
            if (name == "blue") return Team.Blue;
            if (name == "green") return Team.Green;
            throw new FormatException($"Unbekanntes Team \"{name}\" (erlaubt: blue, green).");
        }

        private static BlockBox ReadBox(JsonValue v, string what)
        {
            if (v == null || v.Kind != JsonKind.Object) throw new FormatException($"\"{what}\" muss ein Objekt mit min und max sein.");
            var min = ReadInts(v["min"], what + ".min");
            var max = ReadInts(v["max"], what + ".max");
            if (max[0] <= min[0] || max[1] <= min[1] || max[2] <= min[2]) throw new FormatException($"\"{what}\": max muss in jeder Richtung größer als min sein.");
            return new BlockBox(min[0], min[1], min[2], max[0], max[1], max[2]);
        }

        private static int[] ReadInts(JsonValue v, string what)
        {
            if (v == null || v.Items.Count != 3) throw new FormatException($"\"{what}\" muss drei ganze Zahlen enthalten.");
            return new[] { v.Items[0].AsInt(what), v.Items[1].AsInt(what), v.Items[2].AsInt(what) };
        }

        private static Vec3 ReadVec(JsonValue v, string what)
        {
            if (v == null || v.Items.Count != 3 || v.Items[0].Kind != JsonKind.Number || v.Items[1].Kind != JsonKind.Number || v.Items[2].Kind != JsonKind.Number)
                throw new FormatException($"\"{what}\": Jeder Punkt muss drei Zahlen enthalten.");
            return new Vec3((float)v.Items[0].NumberValue, (float)v.Items[1].NumberValue, (float)v.Items[2].NumberValue);
        }

        private static JsonValue WriteBox(BlockBox b) =>
            JsonValue.NewObject()
                .Set("min", JsonValue.NewArray().Add(JsonValue.Number(b.MinX)).Add(JsonValue.Number(b.MinY)).Add(JsonValue.Number(b.MinZ)))
                .Set("max", JsonValue.NewArray().Add(JsonValue.Number(b.MaxX)).Add(JsonValue.Number(b.MaxY)).Add(JsonValue.Number(b.MaxZ)));
    }
}
