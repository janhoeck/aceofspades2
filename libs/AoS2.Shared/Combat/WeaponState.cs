using AoS2.Shared.Movement;

namespace AoS2.Shared.Combat
{
    /// <summary>Munition einer Waffe.</summary>
    public struct WeaponAmmo
    {
        public short Magazine;
        public short Reserve;

        public WeaponAmmo(int magazine, int reserve)
        {
            Magazine = (short)magazine;
            Reserve = (short)reserve;
        }
    }

    /// <summary>
    /// Waffen eines Spielers: gewählter Platz, Pausen und Munition. Wird wie die Bewegung Tick für Tick aus den Eingaben
    /// gerechnet (<see cref="WeaponSim"/>), auf dem Client als Vorhersage und auf dem Server verbindlich. So zeigt der Client
    /// Mündungsfeuer und Munition sofort an, und der Server weiß aus derselben Rechnung, in welchem Tick geschossen wurde.
    /// </summary>
    public struct WeaponState
    {
        /// <summary>Gewählter Platz: 0 = Spaten (abbauen/bauen), 1 … = Waffen aus weapons.json.</summary>
        public byte Slot;

        /// <summary>Ticks bis zum nächsten möglichen Schuss.</summary>
        public byte Cooldown;

        /// <summary>Ticks, bis die Waffe nach einem Wechsel bereit ist.</summary>
        public byte Switch;

        /// <summary>Ticks, bis das Nachladen fertig ist (0 = lädt nicht).</summary>
        public ushort Reload;

        /// <summary>Feuern war im letzten Tick gedrückt (Waffen ohne Dauerfeuer schießen nur einmal pro Druck).</summary>
        public bool TriggerHeld;

        public WeaponAmmo Ammo1, Ammo2, Ammo3, Ammo4;

        public WeaponAmmo GetAmmo(int slot)
        {
            switch (slot)
            {
                case 1: return Ammo1;
                case 2: return Ammo2;
                case 3: return Ammo3;
                case 4: return Ammo4;
                default: return default;
            }
        }

        public void SetAmmo(int slot, WeaponAmmo ammo)
        {
            switch (slot)
            {
                case 1: Ammo1 = ammo; break;
                case 2: Ammo2 = ammo; break;
                case 3: Ammo3 = ammo; break;
                case 4: Ammo4 = ammo; break;
            }
        }

        /// <summary>Volle Munition für alle Waffen (Spawn), Platz <paramref name="slot"/> gewählt und sofort bereit.</summary>
        public static WeaponState Full(CombatSettings settings, int slot = 0)
        {
            var w = new WeaponState { Slot = (byte)Clamp(slot, settings) };
            foreach (var weapon in settings.Weapons) w.SetAmmo(weapon.Slot, new WeaponAmmo(weapon.Magazine, weapon.Reserve));
            return w;
        }

        internal static int Clamp(int slot, CombatSettings settings) => slot < 0 || slot > settings.SlotCount ? 0 : slot;

        public bool Equals(WeaponState o) =>
            Slot == o.Slot && Cooldown == o.Cooldown && Switch == o.Switch && Reload == o.Reload && TriggerHeld == o.TriggerHeld &&
            Same(Ammo1, o.Ammo1) && Same(Ammo2, o.Ammo2) && Same(Ammo3, o.Ammo3) && Same(Ammo4, o.Ammo4);

        private static bool Same(WeaponAmmo a, WeaponAmmo b) => a.Magazine == b.Magazine && a.Reserve == b.Reserve;
    }

    /// <summary>
    /// Ein Tick der Waffen: Wechsel, Pausen, Nachladen und Feuern. Deterministisch wie <see cref="PlayerMovement"/>:
    /// gleicher Zustand + gleiche Eingabe ergibt auf Client und Server dasselbe.
    /// <list type="bullet">
    /// <item>Wechsel: <see cref="PlayerInput.Weapon"/> wählt den Platz; danach ist die Waffe <c>switchSeconds</c> nicht bereit, Nachladen bricht ab.</item>
    /// <item>Feuern: nur ohne Pause, ohne Nachladen und mit Munition. Waffen ohne Dauerfeuer brauchen einen neuen Druck.</item>
    /// <item>Nachladen: mit der Nachladen-Taste oder automatisch, sobald das Magazin leer ist.</item>
    /// </list>
    /// </summary>
    public static class WeaponSim
    {
        /// <summary>Rechnet einen Tick. Liefert den Platz der Waffe, die in diesem Tick feuert, sonst 0.</summary>
        public static int Step(ref WeaponState w, PlayerInput input, CombatSettings settings)
        {
            var slot = WeaponState.Clamp(input.Weapon, settings);
            if (slot != w.Slot)
            {
                w.Slot = (byte)slot;
                w.Switch = (byte)System.Math.Min(settings.SwitchTicks, byte.MaxValue);
                w.Reload = 0;
            }
            else if (w.Switch > 0) w.Switch--;
            if (w.Cooldown > 0) w.Cooldown--;

            var weapon = settings.Weapon(w.Slot);
            var fire = input.Has(InputButtons.Fire);
            var trigger = fire && (weapon == null || weapon.Automatic || !w.TriggerHeld);
            w.TriggerHeld = fire;
            if (weapon == null) return 0;

            var ammo = w.GetAmmo(w.Slot);
            if (w.Reload > 0)
            {
                if (--w.Reload == 0)
                {
                    var take = System.Math.Min(weapon.Magazine - ammo.Magazine, ammo.Reserve);
                    ammo.Magazine += (short)take;
                    ammo.Reserve -= (short)take;
                    w.SetAmmo(w.Slot, ammo);
                }
                return 0;
            }

            if (input.Has(InputButtons.Reload) && ammo.Magazine < weapon.Magazine && TryStartReload(ref w, weapon, ammo)) return 0;
            if (!trigger || w.Cooldown > 0 || w.Switch > 0) return 0;
            if (ammo.Magazine <= 0)
            {
                TryStartReload(ref w, weapon, ammo);
                return 0;
            }

            ammo.Magazine--;
            w.SetAmmo(w.Slot, ammo);
            w.Cooldown = (byte)weapon.FireIntervalTicks;
            if (ammo.Magazine == 0) TryStartReload(ref w, weapon, ammo); // leer: sofort nachladen
            return w.Slot;
        }

        private static bool TryStartReload(ref WeaponState w, WeaponSettings weapon, WeaponAmmo ammo)
        {
            if (weapon.ReloadTicks <= 0 || ammo.Reserve <= 0) return false;
            w.Reload = (ushort)weapon.ReloadTicks;
            return true;
        }
    }
}
