using System;
using System.Collections.Generic;
using AoS2.Shared.Json;
using AoS2.Shared.Movement;

namespace AoS2.Shared.Combat
{
    /// <summary>
    /// Eine Klasse aus <c>data/classes.json</c> (Miner, Assault, Recon, Rocketeer): Waffen auf den Plätzen, Blockvorrat,
    /// Pause beim Abbauen/Bauen, Tempo und Ausrüstung am Modell. Zeiten sind schon in Ticks umgerechnet.
    /// <para>
    /// Platz 0 ist immer der Spaten, die Waffen der Klasse liegen auf den Plätzen 1 … <see cref="SlotCount"/> (im Spiel die
    /// Tasten 2 … 5). Waffenwechsel, Munition und Feuern rechnet <see cref="WeaponSim"/> mit diesen Plätzen.
    /// </para>
    /// </summary>
    public sealed class PlayerClass
    {
        private readonly List<WeaponSettings> _weapons;

        /// <param name="movement">Bewegung dieser Klasse (Tempo-Faktor und Kollisionshöhe mit Ausrüstung schon eingerechnet).</param>
        public PlayerClass(string name, MovementSettings movement, CombatSettings combat, IEnumerable<WeaponSettings> weapons)
        {
            Name = name;
            DisplayName = name;
            Movement = movement;
            _weapons = new List<WeaponSettings>(weapons ?? Array.Empty<WeaponSettings>());
            if (_weapons.Count > CombatSettings.MaxWeapons) throw new FormatException($"Höchstens {CombatSettings.MaxWeapons} Waffen je Klasse.");
            SwitchTicks = combat?.SwitchTicks ?? 0;
            Health = combat?.Health ?? 100;
        }

        /// <summary>Klassen-ID: Stelle in classes.json (1 = erste Klasse; 0 = keine, für Zuschauer).</summary>
        public byte Id;

        public string Name;
        public string DisplayName;
        public string Description = "";

        /// <summary>Ausrüstung im Spielermodell (Würfel mit <c>gear</c> = diesem Wert), <c>null</c> = keine.</summary>
        public string Gear;

        /// <summary>Blockvorrat beim Spawn (höchstens so viele Blöcke trägt man).</summary>
        public int Blocks;

        /// <summary>Pause zwischen zwei Blockaktionen (abbauen oder bauen) in Ticks.</summary>
        public int EditCooldownTicks = 12;

        /// <summary>Faktor für Gehen, Sprinten und Ducken (1 = movement.json).</summary>
        public float SpeedFactor = 1f;

        public int Health;

        /// <summary>Ticks nach einem Waffenwechsel, bis die Waffe bereit ist (aus combat.json).</summary>
        public int SwitchTicks;

        public MovementSettings Movement;

        /// <summary>Waffen in Platz-Reihenfolge; <c>Weapons[0]</c> liegt auf Platz 1.</summary>
        public IReadOnlyList<WeaponSettings> Weapons => _weapons;

        /// <summary>Höchster gültiger Platz (0 = nur der Spaten).</summary>
        public int SlotCount => _weapons.Count;

        /// <summary>Waffe auf einem Platz, für Platz 0 (Spaten) oder ungültige Plätze <c>null</c>.</summary>
        public WeaponSettings Weapon(int slot) => slot >= 1 && slot <= _weapons.Count ? _weapons[slot - 1] : null;

        /// <summary>Waffen-ID auf einem Platz (0 = Spaten oder leer).</summary>
        public byte WeaponId(int slot) => (byte)(Weapon(slot)?.Id ?? 0);

        public override string ToString() => Name;
    }

    /// <summary>
    /// Alle Klassen aus <c>data/classes.json</c>. Klasse 0 (<see cref="None"/>) hat niemand im Spiel: Zuschauer ohne Team
    /// rechnen mit ihr (keine Waffen, keine Blöcke, keine Ausrüstung).
    /// </summary>
    public sealed class PlayerClasses
    {
        /// <summary>Höchstens so viele Klassen (die ID passt in drei Bit des Snapshots).</summary>
        public const int MaxClasses = 7;

        private readonly List<PlayerClass> _classes = new List<PlayerClass>();

        private PlayerClasses(PlayerClass none)
        {
            None = none;
        }

        public PlayerClass None { get; }

        /// <summary>Wählbare Klassen in der Reihenfolge aus classes.json; <c>All[0]</c> hat die ID 1.</summary>
        public IReadOnlyList<PlayerClass> All => _classes;

        /// <summary>Klasse für Spieler, die keine gewählt haben (<c>defaultClass</c>).</summary>
        public PlayerClass Default { get; private set; }

        /// <summary>Klasse nach ID; 0 = <see cref="None"/>, unbekannte IDs <c>null</c>.</summary>
        public PlayerClass Get(int id) => id == 0 ? None : id >= 1 && id <= _classes.Count ? _classes[id - 1] : null;

        public PlayerClass Find(string name)
        {
            foreach (var c in _classes)
                if (c.Name == name) return c;
            return null;
        }

        /// <summary>Gültige ID einer wählbaren Klasse?</summary>
        public bool IsSelectable(int id) => id >= 1 && id <= _classes.Count;

        /// <summary>
        /// Liest classes.json. Waffen kommen aus <paramref name="combat"/> (über den Namen), Bewegung aus movement.json und die
        /// Kollisionshöhe je Ausrüstung aus <c>stances</c> in player_rig.json.
        /// </summary>
        public static PlayerClasses Parse(string classesJson, CombatSettings combat, string movementJson, string playerRigJson)
        {
            var noneMovement = MovementSettings.Parse(movementJson, playerRigJson, "none");
            var result = new PlayerClasses(new PlayerClass("none", noneMovement, combat, null) { DisplayName = "Keine" });
            var rigClasses = RigClasses(playerRigJson);

            var root = JsonParser.Parse(classesJson);
            var list = root["classes"];
            if (list == null || list.Kind != JsonKind.Array) throw new FormatException("classes.json: \"classes\" muss ein Array sein.");
            if (list.Items.Count == 0) throw new FormatException("classes.json: \"classes\" ist leer.");
            if (list.Items.Count > MaxClasses) throw new FormatException($"classes.json: höchstens {MaxClasses} Klassen.");
            for (var i = 0; i < list.Items.Count; i++)
            {
                var c = list.Items[i];
                var name = c.Kind == JsonKind.Object ? c.GetString("name") : null;
                if (string.IsNullOrEmpty(name)) throw new FormatException($"classes.json: Klasse Nr. {i + 1} hat keinen \"name\".");
                if (name == "none") throw new FormatException("classes.json: \"none\" ist kein erlaubter Klassenname.");
                if (result.Find(name) != null) throw new FormatException($"classes.json: Die Klasse \"{name}\" steht doppelt in der Liste.");
                try
                {
                    var cls = ParseClass(c, name, combat, movementJson, playerRigJson, rigClasses);
                    cls.Id = (byte)(i + 1);
                    result._classes.Add(cls);
                }
                catch (FormatException e)
                {
                    throw new FormatException($"classes.json, Klasse \"{name}\": {e.Message}", e);
                }
            }

            var defaultName = root.GetString("defaultClass");
            result.Default = defaultName == null ? result._classes[0] : result.Find(defaultName)
                ?? throw new FormatException($"classes.json: \"defaultClass\" \"{defaultName}\" ist keine der Klassen.");
            return result;
        }

        private static PlayerClass ParseClass(JsonValue c, string name, CombatSettings combat, string movementJson, string rigJson, ICollection<string> rigClasses)
        {
            var gear = c.GetString("gear");
            if (gear != null && !rigClasses.Contains(gear))
                throw new FormatException($"Ausrüstung \"{gear}\" gibt es in player_rig.json nicht (dort: {string.Join(", ", rigClasses)}).");

            var weaponNames = c["weapons"];
            if (weaponNames == null || weaponNames.Kind != JsonKind.Array) throw new FormatException("\"weapons\" muss ein Array mit Namen aus weapons.json sein.");
            if (weaponNames.Items.Count > CombatSettings.MaxWeapons) throw new FormatException($"höchstens {CombatSettings.MaxWeapons} Waffen (Plätze 2 … {CombatSettings.MaxWeapons + 1}).");
            var weapons = new List<WeaponSettings>();
            foreach (var w in weaponNames.Items)
            {
                if (w.Kind != JsonKind.String) throw new FormatException("\"weapons\" darf nur Namen enthalten.");
                var weapon = combat.Find(w.StringValue) ?? throw new FormatException($"Die Waffe \"{w.StringValue}\" gibt es in weapons.json nicht.");
                if (weapons.Contains(weapon)) throw new FormatException($"Die Waffe \"{w.StringValue}\" steht doppelt in der Liste.");
                weapons.Add(weapon);
            }

            var speed = c.GetFloat("speed", 1f);
            if (speed < 0.5f || speed > 2f) throw new FormatException("\"speed\" muss zwischen 0,5 und 2 liegen.");
            var movement = MovementSettings.Parse(movementJson, rigJson, gear ?? "none");
            movement.WalkSpeed *= speed;
            movement.SprintSpeed *= speed;
            movement.CrouchSpeed *= speed;

            var cls = new PlayerClass(name, movement, combat, weapons)
            {
                DisplayName = c.GetString("displayName", name),
                Description = c.GetString("description", ""),
                Gear = gear,
                Blocks = c.RequireInt("blocks"),
                EditCooldownTicks = (int)Math.Round(c.RequireNumber("editSeconds") * movement.TickRate),
                SpeedFactor = speed,
                Health = c.GetInt("health", combat.Health),
            };
            if (cls.Blocks < 0 || cls.Blocks > 999) throw new FormatException("\"blocks\" muss zwischen 0 und 999 liegen.");
            if (cls.EditCooldownTicks < 1 || cls.EditCooldownTicks > 10 * movement.TickRate)
                throw new FormatException($"\"editSeconds\" muss zwischen {1.0 / movement.TickRate:0.###} und 10 liegen.");
            if (cls.Health < 1 || cls.Health > 255) throw new FormatException("\"health\" muss zwischen 1 und 255 liegen.");
            return cls;
        }

        /// <summary>Klassen mit Ausrüstung im Spielermodell (<c>classes</c> in player_rig.json).</summary>
        private static ICollection<string> RigClasses(string rigJson)
        {
            var names = new List<string>();
            var list = JsonParser.Parse(rigJson)["classes"];
            if (list != null)
                foreach (var v in list.Items)
                    if (v.Kind == JsonKind.String) names.Add(v.StringValue);
            return names;
        }
    }
}
