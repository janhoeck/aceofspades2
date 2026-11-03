namespace AoS2.Shared.Rig
{
    /// <summary>
    /// Drehung + Verschiebung als 3×4-Matrix (die letzte Zeile 0 0 0 1 einer 4×4-Matrix fehlt). Zeile für Zeile:
    /// <c>M00 M01 M02 | M03</c>. Punkte werden als Spalte von rechts multipliziert: <c>p' = R · p + t</c>.
    /// </summary>
    public struct Mat34
    {
        public float M00, M01, M02, M03;
        public float M10, M11, M12, M13;
        public float M20, M21, M22, M23;

        public static readonly Mat34 Identity = new Mat34 { M00 = 1, M11 = 1, M22 = 1 };

        public static Mat34 Translation(Vec3 t) => new Mat34 { M00 = 1, M11 = 1, M22 = 1, M03 = t.X, M13 = t.Y, M23 = t.Z };

        /// <summary>Drehung in Grad, Reihenfolge wie Blockbench: Matrix = Rz · Ry · Rx (erst um x, dann y, dann z).</summary>
        public static Mat34 RotationZYX(Vec3 degrees)
        {
            float cx = DetMath.CosDeg(degrees.X), sx = DetMath.SinDeg(degrees.X);
            float cy = DetMath.CosDeg(degrees.Y), sy = DetMath.SinDeg(degrees.Y);
            float cz = DetMath.CosDeg(degrees.Z), sz = DetMath.SinDeg(degrees.Z);
            // Rz · Ry · Rx ausmultipliziert
            return new Mat34
            {
                M00 = cz * cy,
                M01 = cz * sy * sx - sz * cx,
                M02 = cz * sy * cx + sz * sx,
                M10 = sz * cy,
                M11 = sz * sy * sx + cz * cx,
                M12 = sz * sy * cx - cz * sx,
                M20 = -sy,
                M21 = cy * sx,
                M22 = cy * cx,
            };
        }

        public static Mat34 operator *(Mat34 a, Mat34 b) => new Mat34
        {
            M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            M03 = a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03,
            M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            M13 = a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13,
            M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
            M23 = a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23,
        };

        public Vec3 TransformPoint(Vec3 p) => new Vec3(
            M00 * p.X + M01 * p.Y + M02 * p.Z + M03,
            M10 * p.X + M11 * p.Y + M12 * p.Z + M13,
            M20 * p.X + M21 * p.Y + M22 * p.Z + M23);

        public Vec3 TransformDirection(Vec3 d) => new Vec3(
            M00 * d.X + M01 * d.Y + M02 * d.Z,
            M10 * d.X + M11 * d.Y + M12 * d.Z,
            M20 * d.X + M21 * d.Y + M22 * d.Z);

        public Vec3 Position => new Vec3(M03, M13, M23);

        /// <summary>Spalten der Drehung: wohin die x-, y- und z-Achse des Knochens zeigen.</summary>
        public Vec3 AxisX => new Vec3(M00, M10, M20);
        public Vec3 AxisY => new Vec3(M01, M11, M21);
        public Vec3 AxisZ => new Vec3(M02, M12, M22);
    }
}
