using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class BlockEditingTests
{
    private static readonly MovementSettings Settings = new();
    private static readonly PlayerState Player = new() { Position = new Vec3(16.5f, 4, 16.5f), OnGround = true };

    [Fact]
    public void CanRemove_Rules()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        var r = TestData.Registry;

        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanRemove(world, r, 5, 3, 5, 2));
        Assert.Equal(BlockEditResult.Indestructible, BlockEditing.CanRemove(world, r, 5, 0, 5, 2));
        Assert.Equal(BlockEditResult.NothingThere, BlockEditing.CanRemove(world, r, 5, 10, 5, 2));
        Assert.Equal(BlockEditResult.OutOfReach, BlockEditing.CanRemove(world, r, 5, 3, 5, BlockEditing.Reach + 0.1f));
    }

    [Fact]
    public void CanPlace_Rules()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        var r = TestData.Registry;

        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanPlace(world, r, 18, 4, 16, TestData.Build, 2, Player, Settings));
        Assert.Equal(BlockEditResult.BlockedByPlayer, BlockEditing.CanPlace(world, r, 16, 5, 16, TestData.Build, 1, Player, Settings));
        Assert.Equal(BlockEditResult.Ok, BlockEditing.CanPlace(world, r, 16, 6, 16, TestData.Build, 1, Player, Settings)); // über dem Kopf (1,9 hoch)
        Assert.Equal(BlockEditResult.Occupied, BlockEditing.CanPlace(world, r, 18, 3, 16, TestData.Build, 2, Player, Settings));
        Assert.Equal(BlockEditResult.NotBuildable, BlockEditing.CanPlace(world, r, 18, 4, 16, TestData.Stone, 2, Player, Settings));
        Assert.Equal(BlockEditResult.OutsideWorld, BlockEditing.CanPlace(world, r, 18, 32, 16, TestData.Build, 2, Player, Settings));
    }

    [Fact]
    public void PlayerTouchingBlockFace_IsNotBlocking()
    {
        // Spieler steht genau an der Blockgrenze x = 17 (16,6 + 0,4) → Block bei x = 17 ist erlaubt.
        var player = new PlayerState { Position = new Vec3(16.6f, 4, 16.5f) };
        Assert.False(PlayerMovement.OverlapsBlock(player.Position, Settings.HalfWidth, Settings.StandHeight, 17, 4, 16));
        Assert.True(PlayerMovement.OverlapsBlock(player.Position, Settings.HalfWidth, Settings.StandHeight, 16, 4, 16));
    }
}
