using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class VoxelWorldTests
{
    [Fact]
    public void NewWorld_IsAir_AndAllocatesNoChunks()
    {
        var world = new VoxelWorld(64, 40, 33);

        Assert.Equal((2, 2, 2), (world.ChunksX, world.ChunksY, world.ChunksZ));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(10, 10, 10));
        Assert.Null(world.GetChunkData(0, 0, 0));
    }

    [Fact]
    public void SetBlock_GetBlock_AcrossChunks()
    {
        var world = new VoxelWorld(64, 64, 64);
        world.SetBlock(31, 0, 0, 2);
        world.SetBlock(32, 0, 0, 3);
        world.SetBlock(63, 63, 63, 4);

        Assert.Equal(2, world.GetBlock(31, 0, 0));
        Assert.Equal(3, world.GetBlock(32, 0, 0));
        Assert.Equal(4, world.GetBlock(63, 63, 63));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(30, 0, 0));
    }

    [Fact]
    public void SetBlock_RaisesEvent_OnlyOnChange()
    {
        var world = new VoxelWorld(32, 32, 32);
        var events = new List<(int, int, int, byte, byte)>();
        world.BlockChanged += (x, y, z, o, n) => events.Add((x, y, z, o, n));

        world.SetBlock(1, 2, 3, 5);
        world.SetBlock(1, 2, 3, 5);
        world.SetBlock(1, 2, 3, 0);
        world.SetBlock(4, 4, 4, 0); // Luft auf Luft

        Assert.Equal(new[] { (1, 2, 3, (byte)0, (byte)5), (1, 2, 3, (byte)5, (byte)0) }, events);
    }

    [Fact]
    public void OutsideWorld_ReadsAir_SetThrows()
    {
        var world = new VoxelWorld(32, 32, 32);
        Assert.Equal(BlockRegistry.Air, world.GetBlock(-1, 0, 0));
        Assert.Equal(BlockRegistry.Air, world.GetBlock(0, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.SetBlock(32, 0, 0, 1));
    }

    [Fact]
    public void IsSolidForMovement_WallsAtSidesAndBottom_OpenTop()
    {
        var world = new VoxelWorld(32, 32, 32);
        Assert.True(world.IsSolidForMovement(-1, 5, 5));
        Assert.True(world.IsSolidForMovement(5, 5, 32));
        Assert.True(world.IsSolidForMovement(5, -1, 5));
        Assert.False(world.IsSolidForMovement(5, 32, 5));
        Assert.False(world.IsSolidForMovement(5, 5, 5));
    }

    [Fact]
    public void CopyRegion_MatchesGetBlock_IncludingOutside()
    {
        var world = new VoxelWorld(40, 33, 35);
        var random = new Random(7);
        for (var i = 0; i < 2000; i++) world.SetBlock(random.Next(40), random.Next(33), random.Next(35), (byte)random.Next(1, 5));

        int x0 = 20, y0 = -1, z0 = 30, sx = 34, sy = 36, sz = 10;
        var buffer = new byte[sx * sy * sz];
        world.CopyRegion(x0, y0, z0, sx, sy, sz, buffer);

        for (var y = 0; y < sy; y++)
            for (var z = 0; z < sz; z++)
                for (var x = 0; x < sx; x++)
                    Assert.Equal(world.GetBlock(x0 + x, y0 + y, z0 + z), buffer[(y * sz + z) * sx + x]);
    }
}
