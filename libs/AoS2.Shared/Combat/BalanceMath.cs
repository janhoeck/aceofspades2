using System;
using AoS2.Shared.Rig;

namespace AoS2.Shared.Combat
{
    /// <summary>
    /// Kennzahlen fürs Balancing, gerechnet aus denselben Werten und Regeln wie das Spiel: Treffer bis zum Tod und Zeit bis zum
    /// Tod je Waffe, Trefferzone und Entfernung. Werkzeug dazu: <c>tools/AoS2.Balance</c>.
    /// </summary>
    public static class BalanceMath
    {
        /// <summary>
        /// Schaden eines Treffers, so wie der Server ihn abzieht: Grundschaden mit Abfall über die Entfernung, mal Faktor der Zone,
        /// auf ganze Lebenspunkte aufgerundet.
        /// </summary>
        public static int DamagePerHit(WeaponSettings weapon, HitZone zone, float distance, CombatSettings combat) =>
            (int)Math.Ceiling(weapon.DamageAt(distance) * combat.ZoneFactor(zone) - 0.001f);

        /// <summary>Wie viele Treffer ein Spieler mit <paramref name="health"/> Lebenspunkten aushält (<c>int.MaxValue</c> = nie).</summary>
        public static int HitsToKill(WeaponSettings weapon, HitZone zone, float distance, CombatSettings combat, int health)
        {
            var perHit = DamagePerHit(weapon, zone, distance, combat);
            return perHit <= 0 ? int.MaxValue : (health + perHit - 1) / perHit;
        }

        /// <summary>
        /// Zeit vom ersten bis zum tödlichen Treffer in Sekunden, wenn jeder Schuss trifft und so schnell wie möglich geschossen
        /// wird: zwischen zwei Schüssen die Feuerpause, nach einem leeren Magazin das Nachladen (es beginnt von selbst, der nächste
        /// Schuss geht einen Tick danach). <c>PositiveInfinity</c>, wenn Magazin und Reserve nicht reichen.
        /// </summary>
        public static double TimeToKill(WeaponSettings weapon, int hits, int tickRate)
        {
            if (hits == int.MaxValue || hits > weapon.Magazine + weapon.Reserve) return double.PositiveInfinity;
            if (hits <= 1) return 0;
            var reloads = (hits - 1) / weapon.Magazine;
            if (reloads > 0 && weapon.ReloadTicks <= 0) return double.PositiveInfinity;
            var ticks = (long)(hits - 1 - reloads) * weapon.FireIntervalTicks + (long)reloads * (weapon.ReloadTicks + 1);
            return ticks / (double)tickRate;
        }

        /// <summary>Schaden pro Sekunde bei Dauerfeuer ohne Nachladen (Grundschaden, ohne Zone und Abfall).</summary>
        public static double DamagePerSecond(WeaponSettings weapon, int tickRate) =>
            weapon.FireIntervalTicks <= 0 ? 0 : weapon.Damage * tickRate / (double)weapon.FireIntervalTicks;

        /// <summary>Explosionsschaden in dieser Entfernung vom Mittelpunkt (linear bis zum Rand, wie <see cref="Explosions"/>).</summary>
        public static float ExplosionDamage(ExplosionSettings e, float distance) =>
            distance >= e.Radius ? 0 : e.Damage * (1 - distance / e.Radius);

        /// <summary>Bis zu dieser Entfernung tötet eine Explosion einen Spieler mit <paramref name="health"/> Lebenspunkten (0 = nie).</summary>
        public static float KillRadius(ExplosionSettings e, int health) =>
            e.Damage < health ? 0 : e.Radius * (1 - health / e.Damage);
    }
}
