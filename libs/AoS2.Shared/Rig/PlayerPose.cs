using AoS2.Shared.Combat;

namespace AoS2.Shared.Rig
{
    /// <summary>
    /// <b>Pose-Rechner</b> für Client und Server: Aus Animationszustand, Zeitpunkt, Blickwinkel und gewählter Waffe ergibt
    /// sich für jeden Knochen eine Matrix. Der Client setzt damit die Knochen des sichtbaren Modells, der Server die
    /// Trefferboxen. Beide rechnen mit denselben Zahlen, deshalb trifft man genau das, was man sieht.
    /// <para>
    /// Rechnung je Knochen (wie Blockbench, siehe <c>bone_transform</c> in player_rig.json):
    /// <c>Welt(Knochen) = Welt(Eltern) · T(Drehpunkt − Drehpunkt der Eltern + Verschiebung) · R(Grunddrehung + Drehung)</c>.
    /// Zur Animation aus Blockbench kommen zwei feste Regeln dazu: Der Kopf neigt sich mit dem Blick nach oben/unten, und mit
    /// einer Waffe in der Hand zeigen beide Arme in Blickrichtung (Zielhaltung).
    /// </para>
    /// Modell und Welt: Das Modell liegt in Pixeln (16 pro Block) mit Blick nach −z (Blockbench). In der Welt blickt man bei
    /// Yaw 0 nach +z (Unity). Deshalb wird z gespiegelt, auf Blöcke verkleinert, um den Yaw gedreht und an den Fußpunkt gesetzt
    /// (<see cref="ModelToWorld"/>). Durch die Spiegelung bleibt der rechte Arm rechts.
    /// </summary>
    public sealed class PlayerPose
    {
        /// <summary>Kugel um den Spieler (Mitte über dem Fußpunkt), die in jeder Pose alle Trefferboxen enthält (Grobprüfung).</summary>
        public const float BoundingRadius = 1.6f;

        public static readonly Vec3 BoundingCenter = new Vec3(0, 1f, 0);

        /// <summary>Wie stark der Kopf dem Blick nach oben/unten folgt, und höchstens wie weit (Grad).</summary>
        public const float HeadPitchFactor = 0.75f;
        public const float MaxHeadPitch = 60f;

        private readonly PlayerRig _rig;
        private readonly bool[] _hittable;
        private readonly Mat34[] _bones;
        private readonly Vec3[] _rotation;
        private readonly Vec3[] _position;
        private readonly Vec3[] _blendRotation;
        private readonly Vec3[] _blendPosition;
        private readonly int _head, _body, _armRight, _armLeft, _lowerRight, _lowerLeft;

        /// <param name="minHitboxPixels">Würfel, die in einer Richtung dünner sind (Pixel), sind keine Trefferzone (nur Optik,
        /// z. B. eine Antenne). <c>minHitboxPixels</c> aus combat.json.</param>
        public PlayerPose(PlayerRig rig, float minHitboxPixels = 0)
        {
            _rig = rig;
            _hittable = new bool[rig.Boxes.Count];
            for (var i = 0; i < _hittable.Length; i++) _hittable[i] = IsThickEnough(rig.Boxes[i], minHitboxPixels);
            var n = rig.Bones.Count;
            _bones = new Mat34[n];
            _rotation = new Vec3[n];
            _position = new Vec3[n];
            _blendRotation = new Vec3[n];
            _blendPosition = new Vec3[n];
            _head = rig.BoneIndex("head");
            _body = rig.BoneIndex("body");
            _armRight = rig.BoneIndex("arm_right");
            _armLeft = rig.BoneIndex("arm_left");
            _lowerRight = rig.BoneIndex("arm_lower_right");
            _lowerLeft = rig.BoneIndex("arm_lower_left");
            Evaluate(default, 0, 0, 0);
        }

        public PlayerRig Rig => _rig;

        /// <summary>Ist der Würfel eine Trefferzone (nicht zu dünn)?</summary>
        public bool IsHittable(int box) => _hittable[box];

        /// <summary>Gehört der Würfel zum Grundkörper oder zur Ausrüstung von <paramref name="gearClass"/>?</summary>
        public static bool IsWorn(RigBox box, string gearClass) => box.Gear == null || box.Gear == gearClass;

        private static bool IsThickEnough(RigBox box, float min)
        {
            var size = box.To - box.From;
            min -= 0.001f; // Rundung: 4,7 − 3,7 ist als float etwas weniger als 1
            return Abs(size.X) >= min && Abs(size.Y) >= min && Abs(size.Z) >= min;
        }

        /// <summary>Matrix eines Knochens im Modell (Pixel), nach <see cref="Evaluate"/>.</summary>
        public Mat34 Bone(int index) => _bones[index];

        /// <summary>Lokale Drehung eines Knochens (Grad) nach <see cref="Evaluate"/>, für Tests.</summary>
        public Vec3 LocalRotation(int index) => _rotation[index];

        /// <summary>
        /// Berechnet die Pose zum Server-Tick <paramref name="tick"/> (mit Nachkommastellen).
        /// <paramref name="weaponSlot"/> ≠ 0 = Waffe in der Hand (Zielhaltung der Arme).
        /// </summary>
        public void Evaluate(AnimationState state, double tick, float pitch, int weaponSlot)
        {
            Sample(state.Animation, tick - state.Start, _rotation, _position);
            if (state.IsBlending(tick) && state.Previous != state.Animation)
            {
                Sample(state.Previous, tick - state.PreviousStart, _blendRotation, _blendPosition);
                var w = (float)((tick - state.BlendStart) / PlayerAnimator.BlendTicks);
                if (w < 0) w = 0;
                for (var i = 0; i < _rotation.Length; i++)
                {
                    _rotation[i] = Vec3.Lerp(_blendRotation[i], _rotation[i], w);
                    _position[i] = Vec3.Lerp(_blendPosition[i], _position[i], w);
                }
            }

            pitch = DetMath.Clamp(pitch, -90f, 90f);
            if (_head >= 0) _rotation[_head].X += DetMath.Clamp(-pitch * HeadPitchFactor, -MaxHeadPitch, MaxHeadPitch);
            if (weaponSlot != 0 && _armRight >= 0 && _armLeft >= 0)
            {
                // Arme waagerecht nach vorn (90°), dazu der Blick; eine Neigung des Körpers wird ausgeglichen.
                var aim = 90f - pitch - (_body >= 0 ? _rotation[_body].X : 0);
                _rotation[_armRight] = new Vec3(aim, 8f, 0);
                _rotation[_armLeft] = new Vec3(aim, -32f, 0);
                if (_lowerRight >= 0) _rotation[_lowerRight] = Vec3.Zero;
                if (_lowerLeft >= 0) _rotation[_lowerLeft] = Vec3.Zero;
            }

            var bones = _rig.Bones;
            for (var i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                var parentPivot = b.Parent >= 0 ? bones[b.Parent].Pivot : Vec3.Zero;
                var local = Mat34.Translation(b.Pivot - parentPivot + _position[i]) * Mat34.RotationZYX(_rotation[i]);
                _bones[i] = b.Parent >= 0 ? _bones[b.Parent] * local : local;
            }
        }

        /// <summary>Lokale Werte einer Animation zum Zeitpunkt <paramref name="ticks"/> nach ihrem Start (Grunddrehung eingerechnet).</summary>
        private void Sample(int animation, double ticks, Vec3[] rotation, Vec3[] position)
        {
            var bones = _rig.Bones;
            var anim = animation < _rig.Animations.Count ? _rig.Animations[animation] : null;
            int f0 = 0, f1 = 0;
            var t = 0f;
            if (anim != null)
            {
                var frame = ticks < 0 ? 0 : ticks; // Bilder = Ticks (abgetastet mit der Tickrate)
                var length = anim.LengthFrames;
                if (anim.Loop == AnimationLoop.Loop)
                {
                    frame -= System.Math.Floor(frame / length) * length;
                    f0 = (int)frame;
                    if (f0 >= length) f0 = 0;
                    f1 = f0 + 1;
                }
                else
                {
                    if (frame > length) frame = length;
                    f0 = (int)frame;
                    f1 = f0 < length ? f0 + 1 : f0;
                }
                t = (float)(frame - f0);
            }
            for (var i = 0; i < bones.Count; i++)
            {
                rotation[i] = bones[i].Rotation + Track(anim?.Rotation[i], f0, f1, t);
                position[i] = Track(anim?.Position[i], f0, f1, t);
            }
        }

        private static Vec3 Track(float[] values, int f0, int f1, float t)
        {
            if (values == null) return Vec3.Zero;
            var a = new Vec3(values[f0 * 3], values[f0 * 3 + 1], values[f0 * 3 + 2]);
            var b = new Vec3(values[f1 * 3], values[f1 * 3 + 1], values[f1 * 3 + 2]);
            return Vec3.Lerp(a, b, t);
        }

        /// <summary>
        /// Modell (Pixel, Blick −z) → Welt (Blöcke, Blick in Yaw-Richtung): <c>T(Fuß) · Ry(Yaw) · Spiegelung z · 1/16</c>.
        /// </summary>
        public static Mat34 ModelToWorld(Vec3 feet, float yaw)
        {
            float c = DetMath.CosDeg(yaw) / PlayerRig.PixelsPerBlock, s = DetMath.SinDeg(yaw) / PlayerRig.PixelsPerBlock;
            const float k = 1f / PlayerRig.PixelsPerBlock;
            return new Mat34
            {
                M00 = c, M01 = 0, M02 = -s, M03 = feet.X,
                M10 = 0, M11 = k, M12 = 0, M13 = feet.Y,
                M20 = -s, M21 = 0, M22 = -c, M23 = feet.Z,
            };
        }

        /// <summary>Trefferbox <paramref name="box"/> in der Welt als gedrehter Quader.</summary>
        public Obb BoxInWorld(int box, Mat34 modelToWorld)
        {
            var b = _rig.Boxes[box];
            var bone = modelToWorld * _bones[b.Bone];
            var pivot = _rig.Bones[b.Bone].Pivot;
            var center = (b.From + b.To) * 0.5f - pivot;
            var half = (b.To - b.From) * (0.5f / PlayerRig.PixelsPerBlock);
            return new Obb
            {
                Center = bone.TransformPoint(center),
                AxisX = (bone.AxisX * PlayerRig.PixelsPerBlock).Normalized,
                AxisY = (bone.AxisY * PlayerRig.PixelsPerBlock).Normalized,
                AxisZ = (bone.AxisZ * PlayerRig.PixelsPerBlock).Normalized,
                Half = new Vec3(Abs(half.X), Abs(half.Y), Abs(half.Z)),
            };
        }

        /// <summary>
        /// Strahl gegen die Trefferboxen dieser Pose (Grundkörper plus Ausrüstung von <paramref name="gearClass"/>, <c>null</c> = keine;
        /// ohne zu dünne Würfel, siehe <see cref="IsHittable"/>).
        /// Erst grob gegen die umschließende Kugel, dann gegen jede Box. Liefert die nächste getroffene Box.
        /// </summary>
        public bool Raycast(Vec3 feet, float yaw, string gearClass, Vec3 origin, Vec3 direction, float maxDistance, out float distance, out int box)
        {
            distance = maxDistance;
            box = -1;
            if (!Geometry.RaySphere(origin, direction, feet + BoundingCenter, BoundingRadius, maxDistance)) return false;
            var toWorld = ModelToWorld(feet, yaw);
            var boxes = _rig.Boxes;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (!_hittable[i] || !IsWorn(boxes[i], gearClass)) continue;
                if (!Geometry.RayObb(origin, direction, BoxInWorld(i, toWorld), distance, out var t)) continue;
                distance = t;
                box = i;
            }
            return box >= 0;
        }

        private static float Abs(float v) => v < 0 ? -v : v;
    }
}
