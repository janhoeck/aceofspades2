using System;

namespace AoS2.Shared
{
    /// <summary>
    /// Mathe-Funktionen, die auf Client (Unity/Mono) und Server (.NET) bitgenau dasselbe liefern.
    /// Math.Sin/Cos dürfen das nicht: Jede Laufzeit nutzt ihre eigene Implementierung.
    /// Erlaubt sind nur + - * / und Sqrt (die IEEE-Norm schreibt deren Ergebnis exakt vor).
    /// </summary>
    public static class DetMath
    {
        public const float Pi = 3.14159265f;
        private const float DegToRad = Pi / 180f;

        public static float Sqrt(float x) => MathF.Sqrt(x);

        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

        public static int FloorToInt(float v)
        {
            var i = (int)v;
            return v < i ? i - 1 : i;
        }

        /// <summary>Sinus eines Winkels in Grad (Fehler unter 1e-6).</summary>
        public static float SinDeg(float degrees)
        {
            // Auf [-180, 180) bringen
            var d = degrees - 360f * FloorToInt((degrees + 180f) / 360f);
            // Auf [-90, 90] spiegeln: sin(180 - x) = sin(x)
            if (d > 90f) d = 180f - d;
            else if (d < -90f) d = -180f - d;
            var x = d * DegToRad;
            var x2 = x * x;
            // Taylor-Reihe bis x^11
            return x * (1f - x2 / 6f * (1f - x2 / 20f * (1f - x2 / 42f * (1f - x2 / 72f * (1f - x2 / 110f)))));
        }

        /// <summary>Kosinus eines Winkels in Grad.</summary>
        public static float CosDeg(float degrees) => SinDeg(degrees + 90f);
    }
}
