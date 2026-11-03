using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class RaycastTests
{
    [Fact]
    public void Cast_Down_HitsTopFace()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        Assert.True(VoxelRaycast.Cast(world, new Vec3(5.5f, 10.5f, 5.5f), new Vec3(0, -1, 0), 20, out var hit));

        Assert.Equal((5, 3, 5), (hit.X, hit.Y, hit.Z));
        Assert.Equal((0, 1, 0), (hit.NormalX, hit.NormalY, hit.NormalZ));
        Assert.Equal((5, 4, 5), (hit.PlaceX, hit.PlaceY, hit.PlaceZ));
        Assert.Equal(6.5f, hit.Distance, 4);
    }

    [Fact]
    public void Cast_Sideways_HitsWallWithNormal()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(10, 5, 5, TestData.Stone);

        Assert.True(VoxelRaycast.Cast(world, new Vec3(5.2f, 5.5f, 5.5f), new Vec3(1, 0, 0), 10, out var hit));
        Assert.Equal((10, 5, 5), (hit.X, hit.Y, hit.Z));
        Assert.Equal((-1, 0, 0), (hit.NormalX, hit.NormalY, hit.NormalZ));
        Assert.Equal(4.8f, hit.Distance, 4);
    }

    [Fact]
    public void Cast_Diagonal_FindsBlock()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(8, 8, 8, TestData.Stone);
        var dir = PlayerMovement.LookDirection(45, -35.26439f); // Raumdiagonale nach oben
        Assert.True(VoxelRaycast.Cast(world, new Vec3(4.5f, 4.5f, 4.5f), dir, 10, out var hit));
        Assert.Equal((8, 8, 8), (hit.X, hit.Y, hit.Z));
    }

    [Fact]
    public void Cast_TooShort_Misses()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(10, 5, 5, TestData.Stone);
        Assert.False(VoxelRaycast.Cast(world, new Vec3(5.5f, 5.5f, 5.5f), new Vec3(1, 0, 0), 4, out _));
    }

    [Fact]
    public void Cast_StartInsideBlock_HitsWithZeroNormal()
    {
        var world = TestData.FlatWorld();
        Assert.True(VoxelRaycast.Cast(world, new Vec3(1.5f, 1.5f, 1.5f), new Vec3(0, 1, 0), 5, out var hit));
        Assert.Equal((1, 1, 1, 0, 0, 0), (hit.X, hit.Y, hit.Z, hit.NormalX, hit.NormalY, hit.NormalZ));
    }
}
