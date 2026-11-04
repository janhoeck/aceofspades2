using System.Globalization;
using System.Text;
using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Rig;

namespace AoS2.Balance;

/// <summary>
/// Schreibt die Balancing-Tabelle als Markdown: Klassen, Waffen, Treffer und Zeit bis zum Tod je Zone und Entfernung,
/// Sprengstoff. Alle Zahlen kommen aus <see cref="GameRules"/> und <see cref="BalanceMath"/>, also aus denselben Regeln,
/// mit denen der Server rechnet.
/// </summary>
internal static class BalanceReport
{
    public static readonly IReadOnlyList<float> DefaultDistances = [0, 25, 50, 100, 200];

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly (HitZone Zone, string Name)[] Zones =
        [(HitZone.Head, "Kopf"), (HitZone.Torso, "Rumpf"), (HitZone.Arm, "Arm"), (HitZone.Leg, "Bein")];

    public static string Build(GameRules rules, IReadOnlyList<float> distances)
    {
        var combat = rules.Combat;
        var tickRate = combat.TickRate;
        var md = new StringBuilder();
        md.AppendLine("# Balancing-Tabelle");
        md.AppendLine();
        md.AppendLine("> Erzeugt aus `data/*.json` mit `dotnet run --project tools/AoS2.Balance -- --out docs/balancing.md`. Nicht von Hand bearbeiten:");
        md.AppendLine("> Werte in `data/weapons.json`, `data/classes.json` und `data/combat.json` ändern und neu erzeugen. Anleitung: [game-design.md](game-design.md#balancing).");
        md.AppendLine();
        md.AppendLine($"Leben {combat.Health} (wenn die Klasse nichts anderes sagt), Zonenfaktoren Kopf ×{F(combat.ZoneFactor(HitZone.Head))}, " +
                                $"Rumpf ×{F(combat.ZoneFactor(HitZone.Torso))}, Arm ×{F(combat.ZoneFactor(HitZone.Arm))}, Bein ×{F(combat.ZoneFactor(HitZone.Leg))}, " +
                                $"Waffenwechsel {F(combat.SwitchTicks / (double)tickRate)} s, Respawn {F(combat.RespawnTicks / (double)tickRate)} s.");
        md.AppendLine();

        // ---- Klassen ----
        md.AppendLine("## Klassen");
        md.AppendLine();
        md.AppendLine("| Klasse | Platz 2 … 5 | Blöcke | Pause Abbauen/Bauen | Gehen / Sprinten | Höhe stehend / geduckt | Leben |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in rules.Classes.All)
        {
            var weapons = c.Weapons.Count == 0 ? "–" : string.Join(", ", c.Weapons.Select(w => w.DisplayName));
            md.AppendLine($"| {c.DisplayName}{(c == rules.Classes.Default ? " (Standard)" : "")} | {weapons} | {c.Blocks} | {F(c.EditCooldownTicks / (double)tickRate)} s | " +
                          $"{F(c.Movement.WalkSpeed)} / {F(c.Movement.SprintSpeed)} Blöcke/s | {F(c.Movement.StandHeight, "0.00")} / {F(c.Movement.CrouchHeight, "0.00")} | {c.Health} |");
        }
        md.AppendLine();

        // ---- Gewehre ----
        var hitscan = combat.Weapons.Where(w => w.Kind == WeaponKind.Hitscan).ToList();
        md.AppendLine("## Gewehre");
        md.AppendLine();
        md.AppendLine("| Waffe | Klassen | Schaden | Schuss/s | Schaden/s | Magazin + Reserve | Nachladen | Abfall | Reichweite |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var w in hitscan)
        {
            var falloff = w.HasFalloff ? $"ab {F(w.FalloffStart)}, {F(w.FalloffMinFactor * 100)} % ab {F(w.FalloffEnd)}" : "keiner";
            md.AppendLine($"| {w.DisplayName} | {UsedBy(rules, w)} | {F(w.Damage)} | {F(tickRate / (double)w.FireIntervalTicks)}{(w.Automatic ? "" : " (Einzelfeuer)")} | " +
                          $"{F(BalanceMath.DamagePerSecond(w, tickRate), "0")} | {w.Magazine} + {w.Reserve} | {F(w.ReloadTicks / (double)tickRate)} s | {falloff} | {F(w.Range)} |");
        }
        md.AppendLine();

        md.AppendLine("## Treffer bis zum Tod");
        md.AppendLine();
        md.AppendLine("Je Zone und Entfernung (Blöcke): Treffer bis zum Tod und in Klammern die Zeit vom ersten bis zum tödlichen Treffer, wenn jeder");
        md.AppendLine("Schuss trifft und so schnell wie möglich geschossen wird (mit Nachladen, falls das Magazin nicht reicht). – = außer Reichweite.");
        md.AppendLine();
        var health = combat.Health;
        foreach (var w in hitscan)
        {
            md.AppendLine($"### {w.DisplayName}");
            md.AppendLine();
            md.AppendLine("| Zone | " + string.Join(" | ", distances.Select(d => F(d))) + " |");
            md.AppendLine("|---|" + string.Concat(distances.Select(_ => "---|")));
            foreach (var (zone, name) in Zones)
            {
                var cells = distances.Select(d =>
                {
                    if (d > w.Range) return "–";
                    var hits = BalanceMath.HitsToKill(w, zone, d, combat, health);
                    if (hits == int.MaxValue) return "nie";
                    var time = BalanceMath.TimeToKill(w, hits, tickRate);
                    return double.IsPositiveInfinity(time) ? $"{hits} (Munition reicht nicht)" : $"{hits} ({F(time, "0.00")} s)";
                });
                md.AppendLine($"| {name} | " + string.Join(" | ", cells) + " |");
            }
            md.AppendLine();
        }

        // ---- Sprengstoff ----
        var explosives = combat.Weapons.Where(w => w.IsProjectile).ToList();
        if (explosives.Count > 0)
        {
            md.AppendLine("## Sprengstoff");
            md.AppendLine();
            md.AppendLine("Schaden an Spielern nach Abstand vom Mittelpunkt (ohne Zonen, Deckung hinter Blöcken schützt ganz).");
            md.AppendLine();
            md.AppendLine("| Waffe | Klassen | Anzahl | Mitte | 1 Block | 2 Blöcke | 3 Blöcke | tödlich bis | Radius | Blöcke: Radius / Schaden Mitte |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var w in explosives)
            {
                var e = w.Explosion;
                string At(float d) => F(BalanceMath.ExplosionDamage(e, d), "0");
                var kill = BalanceMath.KillRadius(e, health);
                md.AppendLine($"| {w.DisplayName} | {UsedBy(rules, w)} | {w.Magazine} + {w.Reserve} | {At(0)} | {At(1)} | {At(2)} | {At(3)} | " +
                              $"{(kill > 0 ? F(kill, "0.00") + " Blöcke" : "nie")} | {F(e.Radius)} | {F(e.BlockRadius)} / {F(e.BlockDamage)} |");
            }
            md.AppendLine();
        }
        return md.ToString();
    }

    private static string UsedBy(GameRules rules, WeaponSettings w)
    {
        var names = rules.Classes.All.Where(c => c.Weapons.Contains(w)).Select(c => c.DisplayName).ToList();
        return names.Count == 0 ? "keine" : string.Join(", ", names);
    }

    private static string F(double value, string format = "0.##") => value.ToString(format, German);

}
