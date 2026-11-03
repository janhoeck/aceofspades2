using AoS2.Shared.Combat;

namespace AoS2.Shared.Maps
{
    /// <summary>
    /// Regeln der Zonen einer Map (seit M6). Client (Vorhersage) und Server (verbindlich) rechnen mit genau dieser Klasse:
    /// <list type="bullet">
    /// <item><b>Blöcke:</b> nur in der Kampfzone änderbar (ohne Kampfzone überall) und nie in einer Spawnzone, auch nicht
    ///       durch Explosionen. Unveränderliche Blöcke halten außerdem schwebende Gruppen fest (<see cref="IsAnchor"/>).</item>
    /// <item><b>Gegner:</b> Die Spawnzone eines Teams ist für das andere Team eine unsichtbare Wand (<see cref="BarriersFor"/>,
    ///       gilt im Bewegungscode).</item>
    /// <item><b>Waffen:</b> Wer in einer Spawnzone steht, kann nicht feuern (<see cref="InSpawnZone"/>), und wer dort steht,
    ///       bekommt keinen Schaden. Schüsse und Geschosse halten an der Zonengrenze an (<see cref="Raycast"/>).</item>
    /// </list>
    /// </summary>
    public sealed class MapZones
    {
        /// <summary>Keine Zonen: alles erlaubt (z. B. offline ohne Teams oder in Tests).</summary>
        public static readonly MapZones None = new MapZones(new MapMetadata());

        private readonly BlockBox[] _spawn;
        private readonly Team[] _owner;
        private readonly BlockBox[] _noBarriers = new BlockBox[0];
        private readonly BlockBox[] _blueBarriers;
        private readonly BlockBox[] _greenBarriers;

        public MapZones(MapMetadata metadata)
        {
            CombatZone = metadata.CombatZone;
            var count = metadata.Teams.Count;
            _spawn = new BlockBox[count];
            _owner = new Team[count];
            var i = 0;
            foreach (var t in new[] { Team.Blue, Team.Green })
            {
                if (!metadata.Teams.TryGetValue(t, out var spawn)) continue;
                _spawn[i] = spawn.Zone;
                _owner[i] = t;
                i++;
            }
            _blueBarriers = metadata.Teams.TryGetValue(Team.Green, out var green) ? new[] { green.Zone } : _noBarriers;
            _greenBarriers = metadata.Teams.TryGetValue(Team.Blue, out var blue) ? new[] { blue.Zone } : _noBarriers;
        }

        public BlockBox? CombatZone { get; }

        /// <summary>Darf der Block (x, y, z) abgebaut, gebaut oder gesprengt werden?</summary>
        public bool CanEditBlock(int x, int y, int z)
        {
            if (CombatZone is BlockBox combat && !combat.Contains(x, y, z)) return false;
            return !InAnySpawn(x, y, z);
        }

        /// <summary>Unveränderliche Blöcke verankern Gruppen wie der Boden: Was an ihnen hängt, fällt nicht.</summary>
        public bool IsAnchor(int x, int y, int z) => !CanEditBlock(x, y, z);

        /// <summary>Steht ein Spieler (Fußpunkt) in einer Spawnzone? Dann kann er nicht feuern und nicht getroffen werden.</summary>
        public bool InSpawnZone(Vec3 feet) => SpawnZoneAt(feet) != Team.None;

        /// <summary>Team, dessen Spawnzone den Punkt enthält (sonst <see cref="Team.None"/>).</summary>
        public Team SpawnZoneAt(Vec3 point)
        {
            int x = DetMath.FloorToInt(point.X), y = DetMath.FloorToInt(point.Y), z = DetMath.FloorToInt(point.Z);
            for (var i = 0; i < _spawn.Length; i++)
                if (_spawn[i].Contains(x, y, z)) return _owner[i];
            return Team.None;
        }

        /// <summary>Spawnzonen, die für Spieler dieses Teams eine Wand sind (die des Gegners). Ohne Team keine.</summary>
        public BlockBox[] BarriersFor(Team team) =>
            team == Team.Blue ? _blueBarriers : team == Team.Green ? _greenBarriers : _noBarriers;

        /// <summary>
        /// Wo ein Strahl (Richtung normiert) zum ersten Mal in eine Spawnzone eintritt. Zonen, in denen der Ursprung schon liegt,
        /// zählen nicht. <paramref name="nx"/>/<paramref name="ny"/>/<paramref name="nz"/> = Normale der getroffenen Zonenseite
        /// (zeigt zum Ursprung), wie bei <see cref="Voxels.VoxelHit"/>.
        /// </summary>
        public bool Raycast(Vec3 origin, Vec3 direction, float maxDistance, out float distance, out int nx, out int ny, out int nz)
        {
            distance = maxDistance;
            nx = ny = nz = 0;
            var found = false;
            foreach (var box in _spawn)
            {
                var min = new Vec3(box.MinX, box.MinY, box.MinZ);
                var max = new Vec3(box.MaxX, box.MaxY, box.MaxZ);
                if (Inside(origin, min, max)) continue;
                if (!Geometry.RayAabb(origin, direction, min, max, distance, out var t)) continue;
                found = true;
                distance = t;
                // Eintrittsseite: die Achse, deren Grenze der Strahl beim Eintritt gerade überquert.
                var p = origin + direction * t;
                nx = ny = nz = 0;
                float best = float.MaxValue, d;
                if ((d = Abs(p.X - min.X)) < best && direction.X > 0) { best = d; nx = -1; ny = nz = 0; }
                if ((d = Abs(p.X - max.X)) < best && direction.X < 0) { best = d; nx = 1; ny = nz = 0; }
                if ((d = Abs(p.Y - min.Y)) < best && direction.Y > 0) { best = d; ny = -1; nx = nz = 0; }
                if ((d = Abs(p.Y - max.Y)) < best && direction.Y < 0) { best = d; ny = 1; nx = nz = 0; }
                if ((d = Abs(p.Z - min.Z)) < best && direction.Z > 0) { best = d; nz = -1; nx = ny = 0; }
                if ((d = Abs(p.Z - max.Z)) < best && direction.Z < 0) { nz = 1; nx = ny = 0; }
            }
            return found;
        }

        private bool InAnySpawn(int x, int y, int z)
        {
            foreach (var box in _spawn)
                if (box.Contains(x, y, z)) return true;
            return false;
        }

        private static bool Inside(Vec3 p, Vec3 min, Vec3 max) =>
            p.X >= min.X && p.X < max.X && p.Y >= min.Y && p.Y < max.Y && p.Z >= min.Z && p.Z < max.Z;

        private static float Abs(float v) => v < 0 ? -v : v;
    }
}
