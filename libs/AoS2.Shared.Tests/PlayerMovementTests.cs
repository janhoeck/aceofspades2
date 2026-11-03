using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class PlayerMovementTests
{
    private static readonly MovementSettings Settings = new();

    private static PlayerState Standing(float x, float y, float z) =>
        new() { Position = new Vec3(x, y, z), OnGround = true };

    private static PlayerState Run(VoxelWorld world, PlayerState state, InputButtons buttons, int ticks, float yaw = 0)
    {
        var input = new PlayerInput { Buttons = buttons, Yaw = yaw };
        for (var i = 0; i < ticks; i++) state = PlayerMovement.Step(state, input, world, Settings);
        return state;
    }

    [Fact]
    public void Falling_LandsExactlyOnGround()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        var state = Run(world, new PlayerState { Position = new Vec3(16, 12.3f, 16) }, InputButtons.None, 120);

        Assert.Equal(4f, state.Position.Y);
        Assert.True(state.OnGround);
        Assert.Equal(0f, state.Velocity.Y);
    }

    [Fact]
    public void Walking_ReachesWalkSpeed_InLookDirection()
    {
        var world = TestData.FlatWorld(64, 32, 64);
        var start = Standing(10.5f, 4, 10.5f);
        var state = Run(world, start, InputButtons.Forward, 60, yaw: 90); // 90° = +X

        Assert.Equal(Settings.WalkSpeed, state.Velocity.X, 3);
        Assert.Equal(0f, state.Velocity.Z, 3);
        Assert.InRange(state.Position.X - start.Position.X, 4.2f, 4.5f); // fast 1 s mit 4,5 Blöcken/s
        Assert.Equal(4f, state.Position.Y);
    }

    [Fact]
    public void Sprinting_IsFaster_CrouchingSlower()
    {
        var world = TestData.FlatWorld(64, 32, 64);
        var sprint = Run(world, Standing(10.5f, 4, 10.5f), InputButtons.Forward | InputButtons.Sprint, 30);
        var crouch = Run(world, Standing(10.5f, 4, 10.5f), InputButtons.Forward | InputButtons.Crouch, 30);

        Assert.Equal(Settings.SprintSpeed, sprint.Velocity.Z, 3);
        Assert.Equal(Settings.CrouchSpeed, crouch.Velocity.Z, 3);
        Assert.True(crouch.Crouching);
    }

    [Fact]
    public void Diagonal_IsNotFaster()
    {
        var world = TestData.FlatWorld(64, 32, 64);
        var state = Run(world, Standing(20.5f, 4, 20.5f), InputButtons.Forward | InputButtons.Right, 30);
        var speed = MathF.Sqrt(state.Velocity.X * state.Velocity.X + state.Velocity.Z * state.Velocity.Z);
        Assert.Equal(Settings.WalkSpeed, speed, 3);
    }

    [Fact]
    public void Releasing_Keys_Stops()
    {
        var world = TestData.FlatWorld();
        var state = Run(world, Standing(10.5f, 4, 10.5f), InputButtons.Forward, 30);
        state = Run(world, state, InputButtons.None, 10);
        Assert.Equal(Vec3.Zero, state.Velocity);
    }

    [Fact]
    public void Wall_StopsPlayer_AtBlockBoundary()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        for (var y = 4; y < 8; y++) world.SetBlock(10, y, 16, TestData.Stone);
        for (var y = 4; y < 8; y++) world.SetBlock(10, y, 15, TestData.Stone);

        var state = Run(world, Standing(5.5f, 4, 16), InputButtons.Forward, 120, yaw: 90);

        Assert.Equal(10 - Settings.HalfWidth, state.Position.X, 4);
        Assert.Equal(0f, state.Velocity.X);
        Assert.Equal(4f, state.Position.Y);
    }

    [Fact]
    public void OneBlockStep_IsClimbedWithoutJumping()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        for (var z = 0; z < 32; z++)
            for (var x = 10; x < 32; x++)
                world.SetBlock(x, 4, z, TestData.Stone);

        var state = Run(world, Standing(5.5f, 4, 16.5f), InputButtons.Forward, 120, yaw: 90);

        Assert.Equal(5f, state.Position.Y);
        Assert.True(state.Position.X > 12);
    }

    [Fact]
    public void TwoBlockWall_CannotBeClimbed()
    {
        // Wie im Original: 2 Blöcke sind eine Wand. Die Stufe gibt es nur am Boden, der Sprung reicht 1,25 hoch.
        var world = TestData.FlatWorld(groundTop: 3);
        for (var z = 0; z < 32; z++)
            for (var x = 10; x < 32; x++)
            {
                world.SetBlock(x, 4, z, TestData.Stone);
                world.SetBlock(x, 5, z, TestData.Stone);
            }

        var walked = Run(world, Standing(5.5f, 4, 16.5f), InputButtons.Forward, 120, yaw: 90);
        Assert.Equal(4f, walked.Position.Y);
        Assert.Equal(10 - Settings.HalfWidth, walked.Position.X, 4);

        var jumped = Run(world, walked, InputButtons.Forward | InputButtons.Jump, 180, yaw: 90);
        Assert.Equal(10 - Settings.HalfWidth, jumped.Position.X, 4);
    }

    [Fact]
    public void JumpOntoOneBlock_Works()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        for (var z = 0; z < 32; z++)
            for (var x = 10; x < 32; x++)
                world.SetBlock(x, 4, z, TestData.Stone);
        var state = Run(world, Standing(9f, 4, 16.5f), InputButtons.Forward | InputButtons.Jump, 40, yaw: 90);
        state = Run(world, state, InputButtons.None, 60, yaw: 90);
        Assert.Equal(5f, state.Position.Y);
    }

    [Fact]
    public void Jump_ReachesJumpHeight()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        var state = Standing(16, 4, 16);
        var maxY = 0f;
        state = PlayerMovement.Step(state, new PlayerInput { Buttons = InputButtons.Jump }, world, Settings);
        for (var i = 0; i < 120; i++)
        {
            state = PlayerMovement.Step(state, default, world, Settings);
            maxY = Math.Max(maxY, state.Position.Y);
        }

        Assert.InRange(maxY - 4f, Settings.JumpHeight - 0.1f, Settings.JumpHeight + 0.01f);
        Assert.Equal(4f, state.Position.Y);
        Assert.True(state.OnGround);
    }

    [Fact]
    public void Ceiling_StopsJump()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        world.SetBlock(16, 6, 16, TestData.Stone); // 2 Blöcke über dem Boden, Spieler ist 1,9 hoch
        var state = Run(world, Standing(16.5f, 4, 16.5f), InputButtons.Jump, 10);

        Assert.True(state.Position.Y + Settings.StandHeight <= 6f + 1e-4f);
    }

    [Fact]
    public void Crouching_UnderLowCeiling_CannotStandUp()
    {
        // Durchgang 2 Blöcke hoch (Boden y = 4, Decke ab y = 6). Mit ganzen Blöcken gibt es keinen Spalt
        // zwischen geduckt (1,47) und stehend (1,9), darum sind die Höhen hier für den Test größer gewählt.
        var world = TestData.FlatWorld(groundTop: 3);
        for (var x = 10; x < 20; x++)
            for (var z = 0; z < 32; z++)
                world.SetBlock(x, 6, z, TestData.Stone);
        var settings = new MovementSettings { StandHeight = 2.5f, CrouchHeight = 1.5f };
        var state = new PlayerState { Position = new Vec3(5.5f, 4, 16.5f), OnGround = true };
        var input = new PlayerInput { Buttons = InputButtons.Forward | InputButtons.Crouch, Yaw = 90 };
        for (var i = 0; i < 180; i++) state = PlayerMovement.Step(state, input, world, settings);
        Assert.True(state.Position.X > 11, "geduckt passt man unter die Decke");

        var release = new PlayerInput { Yaw = 90 };
        state = PlayerMovement.Step(state, release, world, settings);
        Assert.True(state.Crouching, "unter der Decke bleibt man geduckt");
    }

    [Fact]
    public void MapEdge_IsAWall()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        var state = Run(world, Standing(2.5f, 4, 16.5f), InputButtons.Back, 120, yaw: 90);
        Assert.Equal(Settings.HalfWidth, state.Position.X, 4);
    }

    [Fact]
    public void Stuck_InsideBlocks_IsPushedUp()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        world.SetBlock(16, 4, 16, TestData.Stone);
        var state = Run(world, Standing(16.5f, 4, 16.5f), InputButtons.None, 1);
        Assert.Equal(5f, state.Position.Y);
    }

    [Fact]
    public void SameInputs_GiveBitIdenticalResults()
    {
        var world = TestData.FlatWorld(64, 32, 64);
        for (var x = 20; x < 40; x++) world.SetBlock(x, 4, 30, TestData.Stone);

        PlayerState Simulate()
        {
            var state = Standing(10.5f, 4, 10.5f);
            var random = new Random(42);
            for (var i = 0; i < 600; i++)
            {
                var input = new PlayerInput { Buttons = (InputButtons)random.Next(0, 128), Yaw = random.Next(-720, 720) * 0.5f };
                state = PlayerMovement.Step(state, input, world, Settings);
            }
            return state;
        }

        var a = Simulate();
        var b = Simulate();
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.X), BitConverter.SingleToInt32Bits(b.Position.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Y), BitConverter.SingleToInt32Bits(b.Position.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Position.Z), BitConverter.SingleToInt32Bits(b.Position.Z));
    }

    [Fact]
    public void Settings_FromRealDataFiles()
    {
        var settings = MovementSettings.Parse(
            File.ReadAllText(TestData.RepoFile("data/movement.json")),
            File.ReadAllText(TestData.RepoFile("data/player_rig.json")));

        Assert.Equal(60, settings.TickRate);
        Assert.Equal(4.5f, settings.WalkSpeed);
        Assert.InRange(settings.StandHeight, 1.8f, 2.0f);
        Assert.InRange(settings.CrouchHeight, 1.4f, 1.6f);
        Assert.True(settings.EyeHeight(false) > settings.EyeHeight(true));
    }

    [Fact]
    public void LookDirection_IsUnitVector()
    {
        foreach (var (yaw, pitch) in new[] { (0f, 0f), (90f, 0f), (33f, -40f), (-120f, 80f) })
            Assert.Equal(1f, PlayerMovement.LookDirection(yaw, pitch).Length, 4);
        Assert.Equal(new Vec3(0, -1, 0).Y, PlayerMovement.LookDirection(0, 90).Y, 4);
    }
}
