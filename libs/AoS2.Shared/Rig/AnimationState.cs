using AoS2.Shared.Movement;

namespace AoS2.Shared.Rig
{
    /// <summary>
    /// Welche Animation ein Spieler gerade zeigt, seit wann, und aus welcher er überblendet wird. Folgt fest aus Werten,
    /// die der Server ohnehin kennt (Haltung, Geschwindigkeit, Boden/Luft, Tick des Wechsels), und ist damit Spielzustand:
    /// Der Server merkt ihn sich pro Tick für den Lag-Ausgleich und schickt ihn in Snapshots an die Clients.
    /// </summary>
    public struct AnimationState
    {
        public byte Animation;

        /// <summary>Server-Tick, zu dem <see cref="Animation"/> bei Bild 0 war.</summary>
        public uint Start;

        /// <summary>Animation vor dem letzten Wechsel (für die Überblendung).</summary>
        public byte Previous;

        public uint PreviousStart;

        /// <summary>Server-Tick des letzten Wechsels; die Überblendung dauert <see cref="PlayerAnimator.BlendTicks"/>.</summary>
        public uint BlendStart;

        /// <summary>Ticks in der Luft (nur Server, nicht im Netz): Erst nach einer kurzen Zeit gilt man als fallend.</summary>
        public ushort AirTicks;

        /// <summary>Wird zum Tick <paramref name="tick"/> noch überblendet?</summary>
        public bool IsBlending(double tick) => tick - BlendStart < PlayerAnimator.BlendTicks;

        public bool SameAs(AnimationState o) =>
            Animation == o.Animation && Start == o.Start && BlendStart == o.BlendStart &&
            (Previous == o.Previous && PreviousStart == o.PreviousStart || BlendStart + PlayerAnimator.BlendTicks <= o.BlendStart);
    }

    /// <summary>
    /// Wählt die Animation aus dem Bewegungszustand und führt <see cref="AnimationState"/> Tick für Tick weiter (nur der Server;
    /// Clients bekommen den Zustand per Snapshot). Animationen heißen wie in Blockbench: idle, walk, run, crouch, crouch_walk, jump, fall.
    /// </summary>
    public static class PlayerAnimator
    {
        /// <summary>Überblendung beim Wechsel: 8 Ticks (gut 0,13 s).</summary>
        public const int BlendTicks = 8;

        /// <summary>So viele Ticks in der Luft (ohne Sprung nach oben), bis „fallen“ gezeigt wird; kurze Stufen nach unten flackern so nicht.</summary>
        public const int FallDelayTicks = 6;

        /// <summary>Langsamer als das (Blöcke/s) gilt als Stehen.</summary>
        public const float MoveThreshold = 0.3f;

        public static AnimationState Start(PlayerRig rig, uint tick)
        {
            var idle = (byte)Index(rig, "idle");
            return new AnimationState { Animation = idle, Previous = idle, Start = tick, PreviousStart = tick, BlendStart = tick < BlendTicks ? 0 : tick - BlendTicks };
        }

        /// <summary>Ein Tick: Animation passend zum Zustand wählen; bei einem Wechsel beginnt die neue bei Bild 0 und wird überblendet.</summary>
        public static void Update(ref AnimationState a, PlayerRig rig, PlayerState s, MovementSettings movement, uint tick)
        {
            a.AirTicks = s.OnGround ? (ushort)0 : (ushort)System.Math.Min(a.AirTicks + 1, ushort.MaxValue);
            var next = (byte)Index(rig, Choose(s, movement, a.AirTicks));
            if (next != a.Animation)
            {
                a.Previous = a.Animation;
                a.PreviousStart = a.Start;
                a.Animation = next;
                a.Start = tick;
                a.BlendStart = tick;
            }
            else if (!a.IsBlending(tick))
            {
                a.Previous = a.Animation;
                a.PreviousStart = a.Start;
            }

            // Start nachziehen, ohne dass sich das Bild ändert: Schleifen um ganze Längen, gehaltene Animationen bis kurz
            // hinter ihr Ende. So bleibt tick − Start immer klein und passt im Snapshot in 16 Bit.
            Normalize(rig, a.Animation, ref a.Start, tick);
            if (!a.IsBlending(tick)) a.PreviousStart = a.Start;
        }

        private static void Normalize(PlayerRig rig, int animation, ref uint start, uint tick)
        {
            var anim = rig.Animations[animation];
            var length = (uint)anim.LengthFrames; // Bilder = Ticks (player_rig.json ist mit der Tickrate abgetastet)
            if (length == 0 || tick - start < length) return;
            if (anim.Loop == AnimationLoop.Loop) start += (tick - start) / length * length;
            else start = tick - length;
        }

        private static string Choose(PlayerState s, MovementSettings movement, int airTicks)
        {
            if (!s.OnGround && (s.Velocity.Y > 0 || airTicks >= FallDelayTicks)) return s.Velocity.Y > 0 ? "jump" : "fall";
            var speed = DetMath.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z);
            if (s.Crouching) return speed > MoveThreshold ? "crouch_walk" : "crouch";
            if (speed > (movement.WalkSpeed + movement.SprintSpeed) / 2) return "run";
            return speed > MoveThreshold ? "walk" : "idle";
        }

        private static int Index(PlayerRig rig, string name)
        {
            var i = rig.AnimationIndex(name);
            if (i >= 0) return i;
            i = rig.AnimationIndex("idle");
            return i >= 0 ? i : 0;
        }
    }
}
