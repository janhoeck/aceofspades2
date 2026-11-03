using AoS2.Shared.Maps;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Movement
{
    /// <summary>
    /// First-Person-Bewegung, ein Aufruf = ein Tick. Client und Server rechnen mit genau diesem Code:
    /// gleicher Zustand + gleiche Eingabe + gleiche Welt ergibt bitgleich denselben neuen Zustand.
    /// Deshalb nur einfache float-Rechnungen (siehe <see cref="DetMath"/>), keine Zufallswerte, keine Uhrzeit.
    ///
    /// Kollision: Der Spieler ist ein senkrechter Quader (Breite 2 × HalfWidth, Höhe je Haltung).
    /// Bewegt wird Achse für Achse (erst y, dann x, dann z); stößt der Quader an einen Block,
    /// bleibt er genau an dessen Grenze stehen.
    ///
    /// Unsichtbare Wände (seit M6): Die Spawnzone des Gegners zählt für die Bewegung wie feste Blöcke (<c>barriers</c>).
    /// Steckt der Spieler schon in einer solchen Zone (z. B. nach einem Teamwechsel), gilt sie für ihn nicht, bis er draußen ist.
    /// </summary>
    public static class PlayerMovement
    {
        private const float Epsilon = 1e-4f;

        public static PlayerState Step(PlayerState state, PlayerInput input, VoxelWorld world, MovementSettings settings) =>
            Step(state, input, world, settings, null);

        /// <param name="barriers">Quader, die zusätzlich zu den Blöcken fest sind (Spawnzonen des Gegners, <see cref="MapZones.BarriersFor"/>).</param>
        public static PlayerState Step(PlayerState state, PlayerInput input, VoxelWorld world, MovementSettings settings, BlockBox[] barriers)
        {
            var dt = settings.DeltaTime;
            var hw = settings.HalfWidth;
            var pos = state.Position;
            var vel = state.Velocity;
            var solid = new Solid(world, barriers, pos, hw, settings.Height(state.Crouching));

            // Steckt der Spieler in Blöcken (z. B. ein Block ist auf ihn gefallen), nach oben herausschieben.
            if (Overlaps(solid, pos, hw, settings.Height(state.Crouching)))
                pos = PushOut(solid, pos, hw, settings.Height(state.Crouching));

            // ---- Haltung ----
            var crouching = state.Crouching;
            if (input.Has(InputButtons.Crouch)) crouching = true;
            else if (crouching && !Overlaps(solid, pos, hw, settings.StandHeight)) crouching = false; // Aufstehen nur mit Platz über dem Kopf
            var height = settings.Height(crouching);

            // ---- Gewünschte Richtung aus Tasten und Blickrichtung ----
            float sin = DetMath.SinDeg(input.Yaw), cos = DetMath.CosDeg(input.Yaw);
            float forward = (input.Has(InputButtons.Forward) ? 1 : 0) - (input.Has(InputButtons.Back) ? 1 : 0);
            float strafe = (input.Has(InputButtons.Right) ? 1 : 0) - (input.Has(InputButtons.Left) ? 1 : 0);
            // vorwärts = (sin, cos), rechts = (cos, -sin)
            var wishX = sin * forward + cos * strafe;
            var wishZ = cos * forward - sin * strafe;
            var wishLength = DetMath.Sqrt(wishX * wishX + wishZ * wishZ);
            if (wishLength > 0)
            {
                wishX /= wishLength;
                wishZ /= wishLength;
            }

            var sprinting = input.Has(InputButtons.Sprint) && forward > 0 && !crouching;
            var speed = crouching ? settings.CrouchSpeed : sprinting ? settings.SprintSpeed : settings.WalkSpeed;

            // ---- Waagerechte Geschwindigkeit nähert sich dem Sollwert ----
            var accel = (state.OnGround ? settings.GroundAcceleration : settings.AirAcceleration) * dt;
            float dvx = wishX * speed - vel.X, dvz = wishZ * speed - vel.Z;
            var dv = DetMath.Sqrt(dvx * dvx + dvz * dvz);
            if (dv <= accel)
            {
                vel.X = wishX * speed;
                vel.Z = wishZ * speed;
            }
            else
            {
                vel.X += dvx * (accel / dv);
                vel.Z += dvz * (accel / dv);
            }

            // ---- Springen und Schwerkraft ----
            var onGround = state.OnGround;
            if (input.Has(InputButtons.Jump) && onGround)
            {
                vel.Y = settings.JumpVelocity;
                onGround = false;
            }
            vel.Y -= settings.Gravity * dt;
            if (vel.Y < -settings.MaxFallSpeed) vel.Y = -settings.MaxFallSpeed;

            // ---- Senkrecht bewegen ----
            var dy = vel.Y * dt;
            if (MoveAxis(solid, ref pos, 1, dy, hw, height))
            {
                onGround = dy < 0;
                vel.Y = 0;
            }
            else onGround = false;

            // ---- Waagerecht bewegen, notfalls eine Stufe hoch ----
            float dx = vel.X * dt, dz = vel.Z * dt;
            var start = pos;
            var flat = pos;
            var blockedX = MoveAxis(solid, ref flat, 0, dx, hw, height);
            var blockedZ = MoveAxis(solid, ref flat, 2, dz, hw, height);
            pos = flat;

            if ((blockedX || blockedZ) && onGround && settings.StepHeight > 0)
            {
                var stepped = start;
                if (!MoveAxis(solid, ref stepped, 1, settings.StepHeight, hw, height))
                {
                    var stepBlockedX = MoveAxis(solid, ref stepped, 0, dx, hw, height);
                    var stepBlockedZ = MoveAxis(solid, ref stepped, 2, dz, hw, height);
                    MoveAxis(solid, ref stepped, 1, -settings.StepHeight, hw, height);
                    // Die Stufe nur nehmen, wenn man damit weiter kommt als ohne.
                    if (HorizontalDistanceSq(start, stepped) > HorizontalDistanceSq(start, flat) + Epsilon)
                    {
                        pos = stepped;
                        blockedX = stepBlockedX;
                        blockedZ = stepBlockedZ;
                    }
                }
            }
            if (blockedX) vel.X = 0;
            if (blockedZ) vel.Z = 0;

            return new PlayerState { Position = pos, Velocity = vel, OnGround = onGround, Crouching = crouching };
        }

        /// <summary>Blickrichtung als Einheitsvektor (für Strahlen beim Abbauen/Bauen und Schießen).</summary>
        public static Vec3 LookDirection(float yaw, float pitch)
        {
            float cp = DetMath.CosDeg(pitch), sp = DetMath.SinDeg(pitch);
            return new Vec3(DetMath.SinDeg(yaw) * cp, -sp, DetMath.CosDeg(yaw) * cp);
        }

        /// <summary>Überschneidet der Spielerquader an <paramref name="feet"/> einen festen Block?</summary>
        public static bool Overlaps(VoxelWorld world, Vec3 feet, float halfWidth, float height) =>
            Overlaps(new Solid(world, null, feet, halfWidth, height), feet, halfWidth, height);

        private static bool Overlaps(in Solid solid, Vec3 feet, float halfWidth, float height)
        {
            Range(feet.X - halfWidth, feet.X + halfWidth, out var x0, out var x1);
            Range(feet.Y, feet.Y + height, out var y0, out var y1);
            Range(feet.Z - halfWidth, feet.Z + halfWidth, out var z0, out var z1);
            return RegionSolid(solid, x0, x1, y0, y1, z0, z1);
        }

        /// <summary>Überschneidet der Spielerquader den Block (bx, by, bz)? Verhindert, dass man in sich selbst baut.</summary>
        public static bool OverlapsBlock(Vec3 feet, float halfWidth, float height, int bx, int by, int bz) =>
            feet.X + halfWidth > bx + Epsilon && feet.X - halfWidth < bx + 1 - Epsilon &&
            feet.Y + height > by + Epsilon && feet.Y < by + 1 - Epsilon &&
            feet.Z + halfWidth > bz + Epsilon && feet.Z - halfWidth < bz + 1 - Epsilon;

        private static Vec3 PushOut(in Solid solid, Vec3 pos, float hw, float height)
        {
            var baseY = DetMath.FloorToInt(pos.Y);
            for (var k = 1; k <= 3; k++)
            {
                var candidate = new Vec3(pos.X, baseY + k, pos.Z);
                if (!Overlaps(solid, candidate, hw, height)) return candidate;
            }
            return pos;
        }

        /// <summary>
        /// Bewegt den Quader entlang einer Achse (0 = x, 1 = y, 2 = z) um <paramref name="delta"/>.
        /// Prüft jede Blockschicht, die die Vorderkante überstreicht; liefert <c>true</c>, wenn ein Block im Weg war.
        /// </summary>
        private static bool MoveAxis(in Solid solid, ref Vec3 pos, int axis, float delta, float hw, float height)
        {
            if (delta == 0f) return false;

            Range(pos.X - hw, pos.X + hw, out var x0, out var x1);
            Range(pos.Y, pos.Y + height, out var y0, out var y1);
            Range(pos.Z - hw, pos.Z + hw, out var z0, out var z1);

            float lo, hi;
            switch (axis)
            {
                case 0: lo = pos.X - hw; hi = pos.X + hw; break;
                case 1: lo = pos.Y; hi = pos.Y + height; break;
                default: lo = pos.Z - hw; hi = pos.Z + hw; break;
            }

            if (delta > 0)
            {
                var end = DetMath.FloorToInt(hi + delta - Epsilon);
                for (var i = DetMath.FloorToInt(hi - Epsilon) + 1; i <= end; i++)
                {
                    if (!LayerSolid(solid, axis, i, x0, x1, y0, y1, z0, z1)) continue;
                    // Vorderkante genau an die Blockgrenze i setzen
                    switch (axis)
                    {
                        case 0: pos.X = i - hw; break;
                        case 1: pos.Y = i - height; break;
                        default: pos.Z = i - hw; break;
                    }
                    return true;
                }
            }
            else
            {
                var end = DetMath.FloorToInt(lo + delta + Epsilon);
                for (var i = DetMath.FloorToInt(lo + Epsilon) - 1; i >= end; i--)
                {
                    if (!LayerSolid(solid, axis, i, x0, x1, y0, y1, z0, z1)) continue;
                    switch (axis)
                    {
                        case 0: pos.X = i + 1 + hw; break;
                        case 1: pos.Y = i + 1; break;
                        default: pos.Z = i + 1 + hw; break;
                    }
                    return true;
                }
            }

            switch (axis)
            {
                case 0: pos.X += delta; break;
                case 1: pos.Y += delta; break;
                default: pos.Z += delta; break;
            }
            return false;
        }

        private static bool LayerSolid(in Solid solid, int axis, int i, int x0, int x1, int y0, int y1, int z0, int z1)
        {
            switch (axis)
            {
                case 0: return RegionSolid(solid, i, i, y0, y1, z0, z1);
                case 1: return RegionSolid(solid, x0, x1, i, i, z0, z1);
                default: return RegionSolid(solid, x0, x1, y0, y1, i, i);
            }
        }

        private static bool RegionSolid(in Solid solid, int x0, int x1, int y0, int y1, int z0, int z1)
        {
            for (var y = y0; y <= y1; y++)
                for (var z = z0; z <= z1; z++)
                    for (var x = x0; x <= x1; x++)
                        if (solid.IsSolid(x, y, z)) return true;
            return false;
        }

        /// <summary>Blöcke, die die Strecke [min, max] berührt (ohne Blöcke, die nur an einer Kante anliegen).</summary>
        private static void Range(float min, float max, out int first, out int last)
        {
            first = DetMath.FloorToInt(min + Epsilon);
            last = DetMath.FloorToInt(max - Epsilon);
        }

        private static float HorizontalDistanceSq(Vec3 a, Vec3 b)
        {
            float dx = b.X - a.X, dz = b.Z - a.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// Was für die Bewegung fest ist: Blöcke (mit Kartenrand und -boden) und die unsichtbaren Wände. Eine Wand, in der der
        /// Quader zu Beginn des Ticks schon steckt, zählt in diesem Tick nicht (so kommt man aus ihr heraus statt festzuhängen).
        /// </summary>
        private readonly struct Solid
        {
            private readonly VoxelWorld _world;
            private readonly BlockBox[] _barriers;
            private readonly int _active; // Bit i = Wand i zählt

            public Solid(VoxelWorld world, BlockBox[] barriers, Vec3 feet, float halfWidth, float height)
            {
                _world = world;
                _barriers = barriers;
                _active = 0;
                if (barriers == null) return;
                Range(feet.X - halfWidth, feet.X + halfWidth, out var x0, out var x1);
                Range(feet.Y, feet.Y + height, out var y0, out var y1);
                Range(feet.Z - halfWidth, feet.Z + halfWidth, out var z0, out var z1);
                for (var i = 0; i < barriers.Length && i < 32; i++)
                {
                    var b = barriers[i];
                    var inside = x1 >= b.MinX && x0 < b.MaxX && y1 >= b.MinY && y0 < b.MaxY && z1 >= b.MinZ && z0 < b.MaxZ;
                    if (!inside) _active |= 1 << i;
                }
            }

            public bool IsSolid(int x, int y, int z)
            {
                if (_world.IsSolidForMovement(x, y, z)) return true;
                if (_active == 0) return false;
                for (var i = 0; i < _barriers.Length && i < 32; i++)
                    if ((_active & (1 << i)) != 0 && _barriers[i].Contains(x, y, z)) return true;
                return false;
            }
        }
    }
}
