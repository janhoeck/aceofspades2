using AoS2.Shared.Combat;
using AoS2.Shared.Movement;
using AoS2.Shared.Rig;

namespace AoS2.Shared.Tests;

/// <summary>
/// Pose-Rechner gegen das Blockbench-Modell: Die Erwartungswerte stammen aus
/// <c>node assets-src/tools/export-rig.mjs assets-src/models/player/player.bbmodel --check &lt;animation&gt; &lt;zeit&gt;</c>,
/// das direkt aus den Keyframes rechnet (gegen Blockbench nachgemessen). Die Zeitpunkte liegen auf ganzen Bildern,
/// dort stimmen Abtastung und Keyframes überein (bis auf die Rundung auf 0,001 px in player_rig.json).
/// </summary>
public class RigTests
{
    private static readonly PlayerRig Rig = PlayerRig.Parse(File.ReadAllText(TestData.RepoFile("data/player_rig.json")));

    private const float Tolerance = 0.01f; // Pixel

    [Fact]
    public void Parse_RealRig_HasBonesBoxesAnimationsAndUvs()
    {
        Assert.Equal(23, Rig.Bones.Count);
        Assert.Equal(64, Rig.Boxes.Count);
        Assert.Equal(7, Rig.Animations.Count);
        Assert.Equal(60, Rig.Fps);
        Assert.Equal(128, Rig.TextureWidth);
        Assert.Equal(-1, Rig.Bones[0].Parent);
        var torso = Rig.Boxes.Single(b => b.Name == "torso_box");
        Assert.Equal(HitZone.Torso, torso.Zone);
        Assert.Null(torso.Gear);
        Assert.Equal(new[] { 74f, 4, 82, 14 }, torso.FaceUvs[0]); // Norden = Vorderseite
        Assert.Equal(HitZone.Head, Rig.Boxes.Single(b => b.Name == "nose").Zone);
        Assert.Equal(HitZone.Leg, Rig.Boxes.Single(b => b.Name == "boot_left").Zone);
        Assert.Equal("miner", Rig.Boxes.Single(b => b.Name == "miner_helmet_shell").Gear);
        Assert.Equal(AnimationLoop.Hold, Rig.Animations[Rig.AnimationIndex("jump")].Loop);
    }

    public static TheoryData<string, float, float, float, float[], float[], float[], float[]> CheckValues => new()
    {
        // Animation, Zeit, maxY, minY, Mitte von nose, glove_right, boot_right, boot_left (Modell, Pixel)
        { "idle", 0f, 29f, 0f, [0, 23.5f, -4], [5.892f, 12.529f, -0.366f], [2, 1.7f, -0.5f], [-2, 1.7f, -0.5f] },
        { "walk", 0.25f, 29.852f, 0f, [0, 24.222f, -4.392f], [5.889f, 13.358f, -0.783f], [2, 2.467f, -0.21f], [-2, 3.111f, 1.98f] },
        { "run", 0.1f, 29.152f, 0.3f, [0.099f, 23.241f, -5.949f], [6.011f, 13.624f, -1.729f], [2, 2.146f, -2.531f], [-2, 5.378f, 5.058f] },
        { "crouch_walk", 0.5f, 23.076f, -0.08f, [-0.354f, 16.857f, -10.316f], [5.518f, 7.021f, -8.034f], [2, 1.851f, -5.984f], [-2, 3.703f, -6.109f] },
        { "jump", 0.3f, 29.167f, -0.688f, [0, 23.409f, -5.13f], [7.514f, 14.606f, -5.116f], [2, 3.082f, -2.224f], [-2, 1.994f, -0.1f] },
    };

    [Theory]
    [MemberData(nameof(CheckValues))]
    public void Pose_MatchesBlockbenchExport(string animation, float time, float maxY, float minY, float[] nose, float[] glove, float[] bootRight, float[] bootLeft)
    {
        var pose = new PlayerPose(Rig);
        var state = new AnimationState { Animation = (byte)Rig.AnimationIndex(animation), Previous = (byte)Rig.AnimationIndex(animation), Start = 1000, BlendStart = 0 };
        pose.Evaluate(state, 1000 + time * 60, 0, 0);

        float top = float.MinValue, bottom = float.MaxValue;
        for (var i = 0; i < Rig.Boxes.Count; i++)
        {
            if (Rig.Boxes[i].Gear != null) continue;
            foreach (var c in ModelCorners(pose, i))
            {
                top = Math.Max(top, c.Y);
                bottom = Math.Min(bottom, c.Y);
            }
        }
        Assert.Equal(maxY, top, Tolerance);
        Assert.Equal(minY, bottom, Tolerance);
        AssertCenter(pose, "nose", nose);
        AssertCenter(pose, "glove_right", glove);
        AssertCenter(pose, "boot_right", bootRight);
        AssertCenter(pose, "boot_left", bootLeft);
    }

    private static IEnumerable<Vec3> ModelCorners(PlayerPose pose, int box)
    {
        var b = Rig.Boxes[box];
        var bone = pose.Bone(b.Bone);
        var pivot = Rig.Bones[b.Bone].Pivot;
        foreach (var x in new[] { b.From.X, b.To.X })
            foreach (var y in new[] { b.From.Y, b.To.Y })
                foreach (var z in new[] { b.From.Z, b.To.Z })
                    yield return bone.TransformPoint(new Vec3(x, y, z) - pivot);
    }

    private static void AssertCenter(PlayerPose pose, string box, float[] expected)
    {
        var corners = ModelCorners(pose, Rig.Boxes.ToList().FindIndex(b => b.Name == box)).ToList();
        var center = corners.Aggregate(Vec3.Zero, (s, c) => s + c) * (1f / corners.Count);
        Assert.Equal(expected[0], center.X, Tolerance);
        Assert.Equal(expected[1], center.Y, Tolerance);
        Assert.Equal(expected[2], center.Z, Tolerance);
    }

    [Fact]
    public void ModelToWorld_FaceLooksAlongYaw_RightArmStaysRight()
    {
        var pose = new PlayerPose(Rig);
        pose.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        var feet = new Vec3(10, 4, 10);
        var nose = Rig.Boxes.ToList().FindIndex(b => b.Name == "nose");
        var glove = Rig.Boxes.ToList().FindIndex(b => b.Name == "glove_right");

        // Yaw 0 = Blick nach +z, rechts = +x
        var w = PlayerPose.ModelToWorld(feet, 0);
        Assert.True(pose.BoxInWorld(nose, w).Center.Z > feet.Z + 0.2f);
        Assert.True(pose.BoxInWorld(glove, w).Center.X > feet.X + 0.3f);
        Assert.Equal(feet.Y + 23.5f / 16, pose.BoxInWorld(nose, w).Center.Y, 1e-4f);

        // Yaw 90 = Blick nach +x, rechts = −z
        w = PlayerPose.ModelToWorld(feet, 90);
        Assert.True(pose.BoxInWorld(nose, w).Center.X > feet.X + 0.2f);
        Assert.True(pose.BoxInWorld(glove, w).Center.Z < feet.Z - 0.3f);
    }

    [Fact]
    public void Look_HeadFollowsPitch_ArmsAimWithWeapon()
    {
        var pose = new PlayerPose(Rig);
        var idle = PlayerAnimator.Start(Rig, 0);
        var feet = new Vec3(10, 4, 10);
        var w = PlayerPose.ModelToWorld(feet, 0);
        var nose = Rig.Boxes.ToList().FindIndex(b => b.Name == "nose");
        var glove = Rig.Boxes.ToList().FindIndex(b => b.Name == "glove_right");

        pose.Evaluate(idle, 0, 0, 0);
        var noseLevel = pose.BoxInWorld(nose, w).Center;
        var gloveDown = pose.BoxInWorld(glove, w).Center;
        pose.Evaluate(idle, 0, 40, 0); // nach unten
        Assert.True(pose.BoxInWorld(nose, w).Center.Y < noseLevel.Y - 0.05f);

        pose.Evaluate(idle, 0, 0, 1); // Waffe: Hand vor dem Körper auf Schulterhöhe
        var gloveAim = pose.BoxInWorld(glove, w).Center;
        Assert.True(gloveAim.Z > gloveDown.Z + 0.4f, $"Hand vorn: {gloveAim} statt {gloveDown}");
        Assert.True(gloveAim.Y > gloveDown.Y + 0.3f);
    }

    [Fact]
    public void BoundingSphere_ContainsEveryBoxInEveryPose()
    {
        var pose = new PlayerPose(Rig);
        var w = PlayerPose.ModelToWorld(Vec3.Zero, 0);
        var worst = 0f;
        for (var a = 0; a < Rig.Animations.Count; a++)
        {
            var state = new AnimationState { Animation = (byte)a, Previous = (byte)a, Start = 0 };
            for (var frame = 0; frame < Rig.Animations[a].Frames; frame += 2)
                foreach (var pitch in new[] { -89f, 0f, 89f })
                    foreach (var weapon in new[] { 0, 1 })
                    {
                        pose.Evaluate(state, frame + 100, pitch, weapon);
                        for (var i = 0; i < Rig.Boxes.Count; i++)
                        {
                            var box = pose.BoxInWorld(i, w);
                            for (var c = 0; c < 8; c++) worst = Math.Max(worst, Vec3.Distance(box.Corner(c), PlayerPose.BoundingCenter));
                        }
                    }
        }
        Assert.True(worst < PlayerPose.BoundingRadius, $"Ecke {worst:0.000} Blöcke von der Mitte entfernt");
    }

    [Fact]
    public void Raycast_HitsVisibleBoxes_ReportsNearestZone()
    {
        var pose = new PlayerPose(Rig);
        pose.Evaluate(PlayerAnimator.Start(Rig, 0), 0, 0, 0);
        var feet = new Vec3(10, 4, 10);

        // Von vorn (Spieler blickt nach +z, Schütze steht bei z = 20) auf Kopfhöhe
        var origin = new Vec3(10, 4 + 23.5f / 16, 20); // Höhe der Nase
        Assert.True(pose.Raycast(feet, 0, null, origin, new Vec3(0, 0, -1), 100, out var distance, out var box));
        Assert.Equal(HitZone.Head, Rig.Boxes[box].Zone);
        Assert.Equal(10 - 4.5f / 16, distance, 0.01f); // Nase steht 4,5 px vor

        // Auf Bauchhöhe: Rumpf
        Assert.True(pose.Raycast(feet, 0, null, new Vec3(10, 4 + 15f / 16, 20), new Vec3(0, 0, -1), 100, out _, out box));
        Assert.Equal(HitZone.Torso, Rig.Boxes[box].Zone);

        // Knapp über dem Kopf vorbei: kein Treffer (ohne Helm)
        Assert.False(pose.Raycast(feet, 0, null, new Vec3(10, 4 + 29.5f / 16, 20), new Vec3(0, 0, -1), 100, out _, out _));
        // Mit Bergbau-Helm (bis 30 px) schon
        Assert.True(pose.Raycast(feet, 0, "miner", new Vec3(10, 4 + 29.5f / 16, 20), new Vec3(0, 0, -1), 100, out _, out box));
        Assert.Equal("miner", Rig.Boxes[box].Gear);

        // Zwischen den Beinen hindurch (Beine 0 … 4 px nach links und rechts, Lücke gibt es nicht): Treffer Bein
        Assert.True(pose.Raycast(feet, 0, null, new Vec3(10.1f, 4 + 3f / 16, 20), new Vec3(0, 0, -1), 100, out _, out box));
        Assert.Equal(HitZone.Leg, Rig.Boxes[box].Zone);
    }

    [Fact]
    public void Blend_StartsAtPreviousPose_EndsAtNewPose()
    {
        var pose = new PlayerPose(Rig);
        var walk = (byte)Rig.AnimationIndex("walk");
        var crouch = (byte)Rig.AnimationIndex("crouch");
        var body = Rig.BoneIndex("body");
        var blend = new AnimationState { Animation = crouch, Start = 100, Previous = walk, PreviousStart = 40, BlendStart = 100 };

        pose.Evaluate(new AnimationState { Animation = walk, Previous = walk, Start = 40 }, 100, 0, 0);
        var walkPos = pose.Bone(body).Position;
        pose.Evaluate(new AnimationState { Animation = crouch, Previous = crouch, Start = 100 }, 100 + PlayerAnimator.BlendTicks, 0, 0);
        var crouchPos = pose.Bone(body).Position;

        pose.Evaluate(blend, 100, 0, 0);
        Assert.Equal(walkPos.Y, pose.Bone(body).Position.Y, 1e-3f);
        pose.Evaluate(blend, 100 + PlayerAnimator.BlendTicks, 0, 0);
        Assert.Equal(crouchPos.Y, pose.Bone(body).Position.Y, 1e-3f);
        pose.Evaluate(blend, 100 + PlayerAnimator.BlendTicks / 2.0, 0, 0);
        var mid = pose.Bone(body).Position.Y;
        Assert.InRange(mid, Math.Min(walkPos.Y, crouchPos.Y) - 0.5f, Math.Max(walkPos.Y, crouchPos.Y) + 0.5f);
    }

    [Fact]
    public void Animator_ChoosesFromMovement_AndKeepsStartClose()
    {
        var m = new MovementSettings();
        var a = PlayerAnimator.Start(Rig, 0);
        string Name() => Rig.Animations[a.Animation].Name;

        PlayerAnimator.Update(ref a, Rig, new PlayerState { OnGround = true }, m, 1);
        Assert.Equal("idle", Name());
        PlayerAnimator.Update(ref a, Rig, new PlayerState { OnGround = true, Velocity = new Vec3(4.5f, 0, 0) }, m, 2);
        Assert.Equal("walk", Name());
        Assert.Equal(2u, a.Start);
        Assert.True(a.IsBlending(5));
        PlayerAnimator.Update(ref a, Rig, new PlayerState { OnGround = true, Velocity = new Vec3(0, 0, 6.5f) }, m, 3);
        Assert.Equal("run", Name());
        PlayerAnimator.Update(ref a, Rig, new PlayerState { OnGround = true, Crouching = true, Velocity = new Vec3(0, 0, 2) }, m, 4);
        Assert.Equal("crouch_walk", Name());
        PlayerAnimator.Update(ref a, Rig, new PlayerState { Velocity = new Vec3(0, 5, 0) }, m, 5);
        Assert.Equal("jump", Name());

        // Kurz in der Luft (Stufe hinunter) bleibt die Animation, erst danach „fallen“.
        a = PlayerAnimator.Start(Rig, 0);
        for (uint t = 1; t < PlayerAnimator.FallDelayTicks; t++) PlayerAnimator.Update(ref a, Rig, new PlayerState { Velocity = new Vec3(0, -1, 0) }, m, t);
        Assert.Equal("idle", Name());
        PlayerAnimator.Update(ref a, Rig, new PlayerState { Velocity = new Vec3(0, -1, 0) }, m, 100);
        Assert.Equal("fall", Name());

        // Lange stehen: Start rückt um ganze Längen nach, das Bild bleibt gleich.
        a = PlayerAnimator.Start(Rig, 0);
        var pose = new PlayerPose(Rig);
        for (uint t = 1; t <= 100_000; t++) PlayerAnimator.Update(ref a, Rig, new PlayerState { OnGround = true }, m, t);
        Assert.True(100_000 - a.Start < 180);
        pose.Evaluate(a, 100_000, 0, 0);
        var head = pose.Bone(Rig.BoneIndex("head")).Position;
        pose.Evaluate(new AnimationState { Animation = a.Animation, Previous = a.Animation, Start = 0 }, 100_000, 0, 0);
        Assert.Equal(head, pose.Bone(Rig.BoneIndex("head")).Position);
    }

    [Fact]
    public void Obb_RotatedBox_RayHitsOnlyInside()
    {
        // Quader 2 × 2 × 2, um 45° um y gedreht: Ecke zeigt zum Strahl
        var s = DetMath.SinDeg(45);
        var box = new Obb { Center = new Vec3(0, 0, 0), AxisX = new Vec3(s, 0, -s), AxisY = new Vec3(0, 1, 0), AxisZ = new Vec3(s, 0, s), Half = new Vec3(1, 1, 1) };
        Assert.True(Geometry.RayObb(new Vec3(0, 0, -10), new Vec3(0, 0, 1), box, 100, out var t));
        Assert.Equal(10 - MathF.Sqrt(2), t, 1e-4f);
        Assert.True(Geometry.RayObb(new Vec3(1.3f, 0, -10), new Vec3(0, 0, 1), box, 100, out _)); // achsenparallel wäre das daneben
        Assert.False(Geometry.RayObb(new Vec3(1.5f, 0, -10), new Vec3(0, 0, 1), box, 100, out _));
        Assert.False(Geometry.RayObb(new Vec3(0, 0, -10), new Vec3(0, 0, 1), box, 5, out _)); // zu kurz
        Assert.False(Geometry.RayObb(new Vec3(0, 0, -10), new Vec3(0, 0, -1), box, 100, out _)); // falsche Richtung
        Assert.True(Geometry.RayObb(new Vec3(0, 0.5f, 0), new Vec3(1, 0, 0), box, 100, out t)); // Ursprung innen
        Assert.Equal(0, t);
    }

    [Fact]
    public void Sphere_CoarseTest()
    {
        Assert.True(Geometry.RaySphere(new Vec3(0, 0, -10), new Vec3(0, 0, 1), Vec3.Zero, 1, 100));
        Assert.False(Geometry.RaySphere(new Vec3(1.1f, 0, -10), new Vec3(0, 0, 1), Vec3.Zero, 1, 100));
        Assert.False(Geometry.RaySphere(new Vec3(0, 0, -10), new Vec3(0, 0, 1), Vec3.Zero, 1, 8.5f));
        Assert.False(Geometry.RaySphere(new Vec3(0, 0, 10), new Vec3(0, 0, 1), Vec3.Zero, 1, 100));
        Assert.True(Geometry.RaySphere(new Vec3(0, 0.5f, 0), new Vec3(0, 0, 1), Vec3.Zero, 1, 0.1f));
    }
}
