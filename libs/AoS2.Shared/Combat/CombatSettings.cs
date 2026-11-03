using System;
using System.Collections.Generic;
using AoS2.Shared.Json;
using AoS2.Shared.Rig;

namespace AoS2.Shared.Combat
{
    public enum WeaponKind
    {
        /// <summary>Strahl, trifft sofort (mit Lag-Ausgleich).</summary>
        Hitscan,

        /// <summary>Geworfen, fällt, prallt von Blöcken ab und explodiert nach der Zündzeit.</summary>
        Grenade,

        /// <summary>Fliegt gerade und explodiert beim Aufprall auf einen Block oder Spieler.</summary>
        Rocket,
    }

    /// <summary>Explosion einer Granate oder Rakete. Schaden nimmt vom Mittelpunkt zum Rand hin linear ab.</summary>
    public sealed class ExplosionSettings
    {
        /// <summary>Reichweite für Spieler (Blöcke) und Schaden im Mittelpunkt.</summary>
        public float Radius;
        public float Damage;

        /// <summary>Reichweite und Schaden im Mittelpunkt für Blöcke (Lebenspunkte aus blocks.json).</summary>
        public float BlockRadius;
        public float BlockDamage;
    }

    /// <summary>Eine Waffe aus <c>data/weapons.json</c>. Zeiten sind schon in Ticks umgerechnet.</summary>
    public sealed class WeaponSettings
    {
        /// <summary>Platz der Waffe (1 = erste Waffe; 0 ist der Spaten).</summary>
        public int Slot;

        public string Name = "";
        public string DisplayName = "";
        public WeaponKind Kind;

        /// <summary>Grundschaden eines Treffers (Hitscan), mal Faktor der Trefferzone.</summary>
        public float Damage;

        public float BlockDamage;
        public float Range = 200f;

        public int FireIntervalTicks;
        public bool Automatic;
        public int Magazine;
        public int Reserve;

        /// <summary>0 = kann nicht nachladen (z. B. Granaten).</summary>
        public int ReloadTicks;

        // ---- Geschosse ----
        public float Speed;
        public float Gravity;
        public float Bounce;
        public float Friction = 1f;
        public int FuseTicks;
        public int LifetimeTicks;
        public ExplosionSettings Explosion;

        public bool IsProjectile => Kind != WeaponKind.Hitscan;
    }

    /// <summary>
    /// Kampfregeln aus <c>data/combat.json</c> und Waffen aus <c>data/weapons.json</c>. Client (Anzeige, Vorhersage von
    /// Munition und Feuerrate) und Server (verbindlich) lesen dieselben Dateien.
    /// </summary>
    public sealed class CombatSettings
    {
        /// <summary>Höchstens so viele Waffen (Plätze 1 … 4) passen in <see cref="WeaponState"/> und ins Protokoll.</summary>
        public const int MaxWeapons = 4;

        public int TickRate = 60;
        public int Health = 100;
        public int RespawnTicks = 300;
        public float SelfDamage = 1f;
        public int SwitchTicks = 18;
        public int MaxRewindTicks = 30;

        private readonly float[] _zoneFactors = { 1f, 1f, 1f, 1f };
        private readonly List<WeaponSettings> _weapons = new List<WeaponSettings>();

        /// <summary>Waffen in Platz-Reihenfolge; <c>Weapons[0]</c> hat Platz 1.</summary>
        public IReadOnlyList<WeaponSettings> Weapons => _weapons;

        /// <summary>Höchster gültiger Platz (0 = nur Spaten).</summary>
        public int SlotCount => _weapons.Count;

        /// <summary>Waffe auf einem Platz, für Platz 0 (Spaten) oder ungültige Plätze <c>null</c>.</summary>
        public WeaponSettings Weapon(int slot) => slot >= 1 && slot <= _weapons.Count ? _weapons[slot - 1] : null;

        public float ZoneFactor(HitZone zone) => _zoneFactors[(int)zone];

        public void SetZoneFactor(HitZone zone, float factor) => _zoneFactors[(int)zone] = factor;

        public void AddWeapon(WeaponSettings weapon)
        {
            if (_weapons.Count >= MaxWeapons) throw new FormatException($"Höchstens {MaxWeapons} Waffen.");
            weapon.Slot = _weapons.Count + 1;
            _weapons.Add(weapon);
        }

        private int Ticks(double seconds) => (int)Math.Round(seconds * TickRate);

        /// <summary>Liest beide Dateien. <paramref name="tickRate"/> kommt aus movement.json (Umrechnung Sekunden → Ticks).</summary>
        public static CombatSettings Parse(string combatJson, string weaponsJson, int tickRate)
        {
            var s = new CombatSettings { TickRate = tickRate };
            var c = JsonParser.Parse(combatJson);
            try
            {
                s.Health = c.GetInt("health", s.Health);
                s.RespawnTicks = s.Ticks(c.GetNumber("respawnSeconds", 5));
                s.SelfDamage = c.GetFloat("selfDamage", s.SelfDamage);
                s.SwitchTicks = s.Ticks(c.GetNumber("switchSeconds", 0.3));
                s.MaxRewindTicks = s.Ticks(c.GetNumber("maxRewindSeconds", 0.5));
                var zones = c["zones"];
                if (zones != null)
                {
                    s.SetZoneFactor(HitZone.Head, zones.GetFloat("head", 1));
                    s.SetZoneFactor(HitZone.Torso, zones.GetFloat("torso", 1));
                    s.SetZoneFactor(HitZone.Arm, zones.GetFloat("arm", 1));
                    s.SetZoneFactor(HitZone.Leg, zones.GetFloat("leg", 1));
                }
            }
            catch (FormatException e)
            {
                throw new FormatException("combat.json: " + e.Message, e);
            }
            if (s.Health < 1 || s.Health > 255) throw new FormatException("combat.json: \"health\" muss zwischen 1 und 255 liegen.");
            if (s.RespawnTicks < 0 || s.MaxRewindTicks < 0 || s.SwitchTicks < 0 || s.SelfDamage < 0)
                throw new FormatException("combat.json: Zeiten und selfDamage dürfen nicht negativ sein.");

            var weapons = JsonParser.Parse(weaponsJson)["weapons"];
            if (weapons == null || weapons.Kind != JsonKind.Array) throw new FormatException("weapons.json: \"weapons\" muss ein Array sein.");
            foreach (var w in weapons.Items)
            {
                var name = w.RequireString("name");
                try
                {
                    s.AddWeapon(ParseWeapon(s, w, name));
                }
                catch (FormatException e)
                {
                    throw new FormatException($"weapons.json, Waffe \"{name}\": {e.Message}", e);
                }
            }
            return s;
        }

        private static WeaponSettings ParseWeapon(CombatSettings s, JsonValue w, string name)
        {
            var weapon = new WeaponSettings
            {
                Name = name,
                DisplayName = w.GetString("displayName", name),
                Kind = ParseKind(w.RequireString("kind")),
                Damage = w.GetFloat("damage", 0),
                BlockDamage = w.GetFloat("blockDamage", 0),
                Range = w.GetFloat("range", 200),
                FireIntervalTicks = Math.Max(1, s.Ticks(w.RequireNumber("fireInterval"))),
                Automatic = w.GetBool("automatic", false),
                Magazine = w.RequireInt("magazine"),
                Reserve = w.GetInt("reserve", 0),
                ReloadTicks = s.Ticks(w.GetNumber("reloadSeconds", 0)),
                Speed = w.GetFloat("speed", 0),
                Gravity = w.GetFloat("gravity", 0),
                Bounce = w.GetFloat("bounce", 0),
                Friction = w.GetFloat("friction", 1),
                FuseTicks = s.Ticks(w.GetNumber("fuseSeconds", 0)),
                LifetimeTicks = s.Ticks(w.GetNumber("lifetimeSeconds", 5)),
            };
            if (weapon.Magazine < 1 || weapon.Magazine > 999 || weapon.Reserve < 0 || weapon.Reserve > 9999)
                throw new FormatException("\"magazine\" muss zwischen 1 und 999 liegen, \"reserve\" zwischen 0 und 9999.");
            if (weapon.Damage < 0 || weapon.BlockDamage < 0 || weapon.Range <= 0) throw new FormatException("Schaden und Reichweite dürfen nicht negativ sein.");
            if (weapon.ReloadTicks > ushort.MaxValue || weapon.FireIntervalTicks > byte.MaxValue)
                throw new FormatException("\"fireInterval\" höchstens 4 s, \"reloadSeconds\" höchstens 18 min.");

            if (weapon.IsProjectile)
            {
                var e = w["explosion"] ?? throw new FormatException("\"explosion\" fehlt.");
                weapon.Explosion = new ExplosionSettings
                {
                    Radius = (float)e.RequireNumber("radius"),
                    Damage = (float)e.RequireNumber("damage"),
                    BlockRadius = e.GetFloat("blockRadius", 0),
                    BlockDamage = e.GetFloat("blockDamage", 0),
                };
                if (weapon.Speed <= 0 || weapon.Explosion.Radius <= 0 || weapon.Explosion.BlockRadius < 0 || weapon.Explosion.BlockRadius > 8)
                    throw new FormatException("\"speed\" und \"explosion.radius\" müssen größer als 0 sein, \"explosion.blockRadius\" zwischen 0 und 8.");
                if (weapon.LifetimeTicks < 1) throw new FormatException("\"lifetimeSeconds\" muss größer als 0 sein.");
            }
            return weapon;
        }

        private static WeaponKind ParseKind(string kind)
        {
            switch (kind)
            {
                case "hitscan": return WeaponKind.Hitscan;
                case "grenade": return WeaponKind.Grenade;
                case "rocket": return WeaponKind.Rocket;
                default: throw new FormatException($"Unbekannte Waffenart \"{kind}\" (hitscan, grenade, rocket).");
            }
        }
    }
}
