using System;

namespace AoS2.Shared.Movement
{
    [Flags]
    public enum InputButtons : ushort
    {
        None = 0,
        Forward = 1 << 0,
        Back = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        Jump = 1 << 4,
        Crouch = 1 << 5,
        Sprint = 1 << 6,
    }

    /// <summary>
    /// Eingabe für einen Simulationsschritt. Genau das schickt der Client ab M4 an den Server.
    /// </summary>
    public struct PlayerInput
    {
        public InputButtons Buttons;

        /// <summary>Blickrichtung waagerecht in Grad: 0 = +Z, 90 = +X (wie Unitys Drehung um y).</summary>
        public float Yaw;

        /// <summary>Blickrichtung senkrecht in Grad: positiv = nach unten (wie Unitys Drehung um x).</summary>
        public float Pitch;

        public bool Has(InputButtons button) => (Buttons & button) != 0;
    }

    /// <summary>Zustand eines Spielers, der von Tick zu Tick weitergerechnet wird.</summary>
    public struct PlayerState
    {
        /// <summary>Fußpunkt: Mitte der Unterseite des Kollisionsquaders.</summary>
        public Vec3 Position;
        public Vec3 Velocity;
        public bool OnGround;
        public bool Crouching;
    }
}
