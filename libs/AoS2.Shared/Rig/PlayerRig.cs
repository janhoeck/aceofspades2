using System;
using System.Collections.Generic;
using AoS2.Shared.Json;

namespace AoS2.Shared.Rig
{
    /// <summary>Trefferzone einer Trefferbox (Faktor je Zone in <c>data/combat.json</c>).</summary>
    public enum HitZone : byte
    {
        Head = 0,
        Torso = 1,
        Arm = 2,
        Leg = 3,
    }

    public enum AnimationLoop
    {
        /// <summary>Beginnt nach dem Ende wieder von vorn.</summary>
        Loop,

        /// <summary>Bleibt nach dem Ende auf dem letzten Bild stehen (auch <c>once</c>).</summary>
        Hold,
    }

    /// <summary>Ein Knochen: Drehpunkt und Grunddrehung im Modell (Pixel, Blockbench-Koordinaten).</summary>
    public sealed class RigBone
    {
        public string Name;

        /// <summary>Index des Elternknochens oder -1. Eltern stehen immer vor ihren Kindern.</summary>
        public int Parent;

        public Vec3 Pivot;

        /// <summary>Grunddrehung in Grad (Reihenfolge ZYX, Matrix = Rz · Ry · Rx).</summary>
        public Vec3 Rotation;
    }

    /// <summary>
    /// Ein Würfel des Modells. Er ist zugleich sichtbar (Client) und Trefferbox (Server), deshalb passen beide immer zusammen.
    /// </summary>
    public sealed class RigBox
    {
        public string Name;
        public int Bone;
        public HitZone Zone;

        /// <summary>Klasse, zu deren Ausrüstung der Würfel gehört (z. B. <c>miner</c>), sonst <c>null</c> (gehört zum Grundkörper).</summary>
        public string Gear;

        /// <summary>Ecken im Modell (Pixel).</summary>
        public Vec3 From, To;

        /// <summary>
        /// Texturkoordinaten je Seite in Pixeln der Textur (<c>u0, v0, u1, v1</c>, v nach unten) in der Reihenfolge
        /// Norden (-z), Osten (+x), Süden (+z), Westen (-x), oben, unten. Ein Eintrag <c>null</c> = Seite ohne Textur.
        /// </summary>
        public float[][] FaceUvs;
    }

    /// <summary>Eine Animation, mit 60 Bildern pro Sekunde abgetastet (zwischen zwei Bildern linear).</summary>
    public sealed class RigAnimation
    {
        public string Name;
        public float Length;
        public AnimationLoop Loop;
        public int Frames;

        /// <summary>Je Knochen die Drehung (Grad) als <c>x, y, z</c> je Bild hintereinander, oder <c>null</c>.</summary>
        public float[][] Rotation;

        /// <summary>Je Knochen die Verschiebung (Pixel) wie <see cref="Rotation"/>, oder <c>null</c>.</summary>
        public float[][] Position;

        /// <summary>Länge in Bildern (= Ticks bei 60 Ticks/s); Bild <see cref="Frames"/> − 1 ist das Ende.</summary>
        public int LengthFrames => Frames - 1;
    }

    /// <summary>
    /// Skelett, Würfel (Trefferboxen) und Animationen des Spielermodells aus <c>data/player_rig.json</c>
    /// (geschrieben von <c>assets-src/tools/export-rig.mjs</c> aus dem Blockbench-Modell). Pose berechnen: <see cref="PlayerPose"/>.
    /// </summary>
    public sealed class PlayerRig
    {
        public const float PixelsPerBlock = 16f;

        private readonly Dictionary<string, int> _boneIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _animationIndex = new Dictionary<string, int>();

        public IReadOnlyList<RigBone> Bones { get; private set; }
        public IReadOnlyList<RigBox> Boxes { get; private set; }
        public IReadOnlyList<RigAnimation> Animations { get; private set; }

        /// <summary>Bilder pro Sekunde der abgetasteten Animationen.</summary>
        public int Fps { get; private set; } = 60;

        public int TextureWidth { get; private set; } = 16;
        public int TextureHeight { get; private set; } = 16;

        /// <summary>Index eines Knochens nach Name, -1 wenn es ihn nicht gibt.</summary>
        public int BoneIndex(string name) => _boneIndex.TryGetValue(name, out var i) ? i : -1;

        /// <summary>Index einer Animation nach Name, -1 wenn es sie nicht gibt.</summary>
        public int AnimationIndex(string name) => _animationIndex.TryGetValue(name, out var i) ? i : -1;

        public static PlayerRig Parse(string json)
        {
            var root = JsonParser.Parse(json);
            try
            {
                return Parse(root);
            }
            catch (FormatException e)
            {
                throw new FormatException("player_rig.json: " + e.Message, e);
            }
        }

        private static PlayerRig Parse(JsonValue root)
        {
            var rig = new PlayerRig();
            var sampling = root["sampling"];
            if (sampling != null) rig.Fps = sampling.GetInt("fps", 60);
            var size = root["texture_size"];
            if (size != null && size.Items.Count == 2)
            {
                rig.TextureWidth = size.Items[0].AsInt("texture_size");
                rig.TextureHeight = size.Items[1].AsInt("texture_size");
            }

            var bones = new List<RigBone>();
            foreach (var b in Require(root, "bones").Items)
            {
                var name = b.RequireString("name");
                var parentName = b.GetString("parent");
                var parent = -1;
                if (parentName != null && !rig._boneIndex.TryGetValue(parentName, out parent))
                    throw new FormatException($"Knochen \"{name}\": Elternknochen \"{parentName}\" fehlt oder steht danach.");
                if (rig._boneIndex.ContainsKey(name)) throw new FormatException($"Knochen \"{name}\" doppelt.");
                rig._boneIndex.Add(name, bones.Count);
                bones.Add(new RigBone { Name = name, Parent = parent, Pivot = ReadVec(b["pivot"], "pivot"), Rotation = ReadVec(b["rotation"], "rotation") });
            }

            var boxes = new List<RigBox>();
            foreach (var h in Require(root, "hitboxes").Items)
            {
                var name = h.RequireString("name");
                var boneName = h.RequireString("bone");
                if (!rig._boneIndex.TryGetValue(boneName, out var bone)) throw new FormatException($"Trefferbox \"{name}\": Knochen \"{boneName}\" fehlt.");
                boxes.Add(new RigBox
                {
                    Name = name,
                    Bone = bone,
                    Zone = ParseZone(h.GetString("zone"), name),
                    Gear = h.GetString("gear"),
                    From = ReadVec(h["from"], "from"),
                    To = ReadVec(h["to"], "to"),
                    FaceUvs = ReadUvs(h["uv"]),
                });
            }

            var animations = new List<RigAnimation>();
            foreach (var a in Require(root, "animations").Items)
            {
                var name = a.RequireString("name");
                var anim = new RigAnimation
                {
                    Name = name,
                    Length = (float)a.RequireNumber("length"),
                    Loop = a.GetString("loop", "loop") == "loop" ? AnimationLoop.Loop : AnimationLoop.Hold,
                    Frames = a.RequireInt("frames"),
                    Rotation = new float[bones.Count][],
                    Position = new float[bones.Count][],
                };
                if (anim.Frames < 2) throw new FormatException($"Animation \"{name}\": zu wenige Bilder.");
                var tracks = a["tracks"];
                if (tracks != null)
                {
                    foreach (var track in tracks.Properties)
                    {
                        if (!rig._boneIndex.TryGetValue(track.Key, out var bone)) throw new FormatException($"Animation \"{name}\": Knochen \"{track.Key}\" fehlt.");
                        anim.Rotation[bone] = ReadFrames(track.Value["rotation"], anim.Frames, name);
                        anim.Position[bone] = ReadFrames(track.Value["position"], anim.Frames, name);
                    }
                }
                rig._animationIndex[name] = animations.Count;
                animations.Add(anim);
            }

            rig.Bones = bones;
            rig.Boxes = boxes;
            rig.Animations = animations;
            return rig;
        }

        private static JsonValue Require(JsonValue root, string name)
        {
            var v = root[name];
            if (v == null || v.Kind != JsonKind.Array) throw new FormatException($"\"{name}\" muss ein Array sein.");
            return v;
        }

        private static HitZone ParseZone(string zone, string box)
        {
            switch (zone)
            {
                case "head": return HitZone.Head;
                case "torso": return HitZone.Torso;
                case "arm": return HitZone.Arm;
                case "leg": return HitZone.Leg;
                default: throw new FormatException($"Trefferbox \"{box}\": unbekannte Zone \"{zone}\".");
            }
        }

        private static Vec3 ReadVec(JsonValue v, string what)
        {
            if (v == null || v.Kind != JsonKind.Array || v.Items.Count != 3) throw new FormatException($"\"{what}\" muss drei Zahlen enthalten.");
            return new Vec3(Number(v.Items[0], what), Number(v.Items[1], what), Number(v.Items[2], what));
        }

        private static float Number(JsonValue v, string what)
        {
            if (v.Kind != JsonKind.Number) throw new FormatException($"\"{what}\" muss Zahlen enthalten.");
            return (float)v.NumberValue;
        }

        private static readonly string[] FaceNames = { "north", "east", "south", "west", "up", "down" };

        private static float[][] ReadUvs(JsonValue uv)
        {
            var faces = new float[FaceNames.Length][];
            if (uv == null || uv.Kind != JsonKind.Object) return faces;
            for (var f = 0; f < FaceNames.Length; f++)
            {
                var v = uv[FaceNames[f]];
                if (v == null || v.IsNull) continue;
                if (v.Kind != JsonKind.Array || v.Items.Count != 4) throw new FormatException($"\"uv.{FaceNames[f]}\" muss vier Zahlen enthalten.");
                faces[f] = new float[4];
                for (var i = 0; i < 4; i++) faces[f][i] = Number(v.Items[i], "uv");
            }
            return faces;
        }

        private static float[] ReadFrames(JsonValue v, int frames, string anim)
        {
            if (v == null || v.IsNull) return null;
            if (v.Kind != JsonKind.Array || v.Items.Count != frames * 3)
                throw new FormatException($"Animation \"{anim}\": Spur mit {v.Items.Count} statt {frames * 3} Werten.");
            var values = new float[v.Items.Count];
            for (var i = 0; i < values.Length; i++) values[i] = Number(v.Items[i], anim);
            return values;
        }
    }
}
