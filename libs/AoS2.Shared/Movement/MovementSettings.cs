using System;
using AoS2.Shared.Json;

namespace AoS2.Shared.Movement
{
    /// <summary>
    /// Werte der Spielerbewegung. Kommen aus <c>data/movement.json</c> (Geschwindigkeiten, Sprung, Schwerkraft)
    /// und <c>data/player_rig.json</c> (Höhe des Kollisionsquaders je Haltung, Feld <c>stances</c>).
    /// Alle Längen in Blöcken, Zeiten in Sekunden.
    /// </summary>
    public sealed class MovementSettings
    {
        /// <summary>Simulationsschritte pro Sekunde (wie der Server).</summary>
        public int TickRate = 60;

        public float WalkSpeed = 4.5f;
        public float SprintSpeed = 6.5f;
        public float CrouchSpeed = 2.2f;

        /// <summary>Wie schnell die Geschwindigkeit am Boden bzw. in der Luft den Sollwert erreicht (Blöcke/s²).</summary>
        public float GroundAcceleration = 60f;
        public float AirAcceleration = 12f;

        public float JumpHeight = 1.25f;
        public float Gravity = 25f;
        public float MaxFallSpeed = 40f;

        /// <summary>Kanten bis zu dieser Höhe steigt man ohne Sprung hoch.</summary>
        public float StepHeight = 1f;

        /// <summary>Halbe Breite des Kollisionsquaders (der Quader ist quadratisch).</summary>
        public float HalfWidth = 0.4f;

        /// <summary>Höhe des Kollisionsquaders stehend/geduckt (aus player_rig.json).</summary>
        public float StandHeight = 1.9f;
        public float CrouchHeight = 1.47f;

        /// <summary>Abstand der Augen (Kamera) unter der Oberkante des Quaders.</summary>
        public float EyeBelowTop = 0.28f;

        public float DeltaTime => 1f / TickRate;

        /// <summary>Anfangsgeschwindigkeit nach oben, mit der man genau <see cref="JumpHeight"/> erreicht: v = √(2·g·h).</summary>
        public float JumpVelocity => DetMath.Sqrt(2f * Gravity * JumpHeight);

        public float Height(bool crouching) => crouching ? CrouchHeight : StandHeight;
        public float EyeHeight(bool crouching) => Height(crouching) - EyeBelowTop;

        /// <summary>
        /// Liest die Werte. <paramref name="playerRigJson"/> darf fehlen (dann bleiben die Standardhöhen);
        /// <paramref name="rigClass"/> wählt die Ausrüstung (<c>none</c> = ohne Helm).
        /// </summary>
        public static MovementSettings Parse(string movementJson, string playerRigJson = null, string rigClass = "none")
        {
            var s = new MovementSettings();
            var m = JsonParser.Parse(movementJson);
            try
            {
                s.TickRate = m.GetInt("tickRate", s.TickRate);
                s.WalkSpeed = m.GetFloat("walkSpeed", s.WalkSpeed);
                s.SprintSpeed = m.GetFloat("sprintSpeed", s.SprintSpeed);
                s.CrouchSpeed = m.GetFloat("crouchSpeed", s.CrouchSpeed);
                s.GroundAcceleration = m.GetFloat("groundAcceleration", s.GroundAcceleration);
                s.AirAcceleration = m.GetFloat("airAcceleration", s.AirAcceleration);
                s.JumpHeight = m.GetFloat("jumpHeight", s.JumpHeight);
                s.Gravity = m.GetFloat("gravity", s.Gravity);
                s.MaxFallSpeed = m.GetFloat("maxFallSpeed", s.MaxFallSpeed);
                s.StepHeight = m.GetFloat("stepHeight", s.StepHeight);
                s.HalfWidth = m.GetFloat("halfWidth", s.HalfWidth);
                s.EyeBelowTop = m.GetFloat("eyeBelowTop", s.EyeBelowTop);
            }
            catch (FormatException e)
            {
                throw new FormatException("movement.json: " + e.Message, e);
            }

            if (playerRigJson != null)
            {
                var stances = JsonParser.Parse(playerRigJson)["stances"] ?? throw new FormatException("player_rig.json: \"stances\" fehlt.");
                s.StandHeight = StanceHeight(stances, "stand", rigClass);
                s.CrouchHeight = StanceHeight(stances, "crouch", rigClass);
            }

            if (s.TickRate <= 0 || s.HalfWidth <= 0 || s.HalfWidth >= 0.5f || s.CrouchHeight <= 0 || s.StandHeight < s.CrouchHeight || s.Gravity <= 0)
                throw new FormatException("Bewegungswerte ungültig (TickRate > 0, 0 < halfWidth < 0,5, 0 < Höhe geduckt ≤ Höhe stehend, gravity > 0).");
            return s;
        }

        private static float StanceHeight(JsonValue stances, string stance, string rigClass)
        {
            var entry = stances[stance]?[rigClass] ?? throw new FormatException($"player_rig.json: stances.{stance}.{rigClass} fehlt.");
            return (float)entry.RequireNumber("height_blocks");
        }
    }
}
