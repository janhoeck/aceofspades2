using System;

namespace AoS2.Shared
{
    /// <summary>
    /// Einfacher 3D-Vektor für den gemeinsamen Code. Bewusst nicht System.Numerics.Vector3:
    /// Dessen SIMD-Beschleunigung kann je nach Laufzeit (Unity/Mono gegen .NET) anders runden.
    /// Hier stehen nur Grundrechenarten, die überall gleich rechnen.
    /// Koordinaten wie in Unity: x = rechts, y = oben, z = vorne. 1 Einheit = 1 Block.
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);

        public float Length => DetMath.Sqrt(X * X + Y * Y + Z * Z);
        public float LengthSquared => X * X + Y * Y + Z * Z;

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

        public static float Distance(Vec3 a, Vec3 b) => (b - a).Length;

        /// <summary>Gleiche Richtung, Länge 1 (der Nullvektor bleibt null).</summary>
        public Vec3 Normalized
        {
            get
            {
                var length = Length;
                return length > 0 ? new Vec3(X / length, Y / length, Z / length) : Zero;
            }
        }

        public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Vec3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode();
            }
        }

        public override string ToString() =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }
}
