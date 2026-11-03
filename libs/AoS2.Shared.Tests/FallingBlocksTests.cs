using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class FallingBlocksTests
{
    private const byte S = TestData.Stone;

    /// <summary>Säule auf dem Boden mit einer Plattform oben drauf (T-Form), Säule bei (10, 4..7, 10).</summary>
    private static VoxelWorld TShape()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        for (var y = 4; y <= 7; y++) world.SetBlock(10, y, 10, S);
        for (var x = 8; x <= 12; x++)
            for (var z = 8; z <= 12; z++)
                world.SetBlock(x, 8, z, S);
        return world;
    }

    [Fact]
    public void RemovingPillar_DetachesPlatform()
    {
        var world = TShape();
        world.SetBlock(10, 5, 10, 0);

        var groups = FloatingBlocks.FindDetached(world, 10, 5, 10);

        var group = Assert.Single(groups);
        Assert.Equal(25 + 2, group.Count); // Plattform + zwei Säulenblöcke darüber
        Assert.All(group, b => Assert.Equal(S, b.Id));
        Assert.DoesNotContain(group, b => b.Y == 4);
    }

    [Fact]
    public void SecondSupport_KeepsStructureAnchored()
    {
        var world = TShape();
        for (var y = 4; y <= 7; y++) world.SetBlock(12, y, 12, S); // zweite Säule
        world.SetBlock(10, 5, 10, 0);

        var groups = FloatingBlocks.FindDetached(world, 10, 5, 10);

        // Oben bleibt alles hängen; nur nichts fällt.
        Assert.Empty(groups);
        Assert.True(FloatingBlocks.IsAnchored(world, 10, 8, 10));
    }

    [Fact]
    public void EdgeContact_DoesNotConnect()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        world.SetBlock(10, 4, 10, S);
        world.SetBlock(10, 5, 10, S);
        world.SetBlock(11, 6, 10, S); // berührt (10, 5) nur an einer Kante
        world.SetBlock(10, 6, 10, S);
        world.SetBlock(10, 6, 10, 0);

        var group = Assert.Single(FloatingBlocks.FindDetached(world, 10, 6, 10));
        Assert.Equal((11, 6, 10), (group[0].X, group[0].Y, group[0].Z));
    }

    [Fact]
    public void OneRemoval_CanDetachTwoGroups()
    {
        var world = TestData.FlatWorld(groundTop: 3);
        // Waagerechter Balken, nur in der Mitte gestützt; Mitte weg = zwei Hälften fallen.
        for (var x = 5; x <= 15; x++) world.SetBlock(x, 8, 10, S);
        for (var y = 4; y <= 7; y++) world.SetBlock(10, y, 10, S);
        world.SetBlock(10, 8, 10, 0);

        var groups = FloatingBlocks.FindDetached(world, 10, 8, 10);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { 5, 5 }, groups.Select(g => g.Count).OrderBy(c => c));
    }

    [Fact]
    public void GroupLargerThanLimit_CountsAsAnchored()
    {
        var world = TShape();
        world.SetBlock(10, 5, 10, 0);

        Assert.Empty(FloatingBlocks.FindDetached(world, 10, 5, 10, maxGroupSize: 10));
        Assert.Single(FloatingBlocks.FindDetached(world, 10, 5, 10, maxGroupSize: 27));
    }

    [Fact]
    public void BottomLayer_IsGround()
    {
        var world = new VoxelWorld(32, 32, 32);
        world.SetBlock(5, 0, 5, S);
        world.SetBlock(5, 1, 5, S);
        world.SetBlock(5, 2, 5, S);
        world.SetBlock(6, 2, 5, S);

        Assert.True(FloatingBlocks.IsAnchored(world, 6, 2, 5));
        world.SetBlock(5, 1, 5, 0);
        Assert.False(FloatingBlocks.IsAnchored(world, 6, 2, 5));
        Assert.False(FloatingBlocks.IsAnchored(world, 5, 1, 5)); // Luft
    }

    [Fact]
    public void Falling_LandsOnGround_KeepsShape()
    {
        var world = TShape();
        var falling = new FallingBlocks(world);
        int started = 0, landedOffset = -1;
        falling.Started += _ => started++;
        falling.Landed += (_, offset) => landedOffset = offset;

        world.SetBlock(10, 5, 10, 0);
        Assert.Equal(1, falling.CheckAfterRemoval(10, 5, 10));
        Assert.Equal(1, started);
        Assert.False(world.IsSolid(10, 8, 10)); // während des Falls nicht in der Welt

        for (var i = 0; i < 300 && falling.Groups.Count > 0; i++) falling.Tick(1f / 60);

        Assert.Empty(falling.Groups);
        // Unterster Block der Gruppe war (10, 6); darunter liegt noch der Säulenstumpf (10, 4) → Landung auf y = 5.
        Assert.Equal(1, landedOffset);
        Assert.True(world.IsSolid(10, 5, 10));
        Assert.True(world.IsSolid(10, 6, 10));
        for (var x = 8; x <= 12; x++)
            for (var z = 8; z <= 12; z++)
                Assert.True(world.IsSolid(x, 7, z));
        Assert.False(world.IsSolid(10, 8, 10));
    }

    [Fact]
    public void Falling_FarDrop_StopsAtFirstObstacle()
    {
        var world = TestData.FlatWorld(32, 64, 32, groundTop: 3);
        world.SetBlock(5, 40, 5, S);
        world.SetBlock(6, 40, 5, S);
        world.SetBlock(6, 10, 5, S);
        world.SetBlock(6, 9, 5, S);
        world.SetBlock(6, 8, 5, S);
        // Hindernis: Turm unter (6, 40) bis y = 10, nicht mit der Gruppe verbunden
        for (var y = 4; y < 8; y++) world.SetBlock(6, y, 5, S);

        var falling = new FallingBlocks(world);
        falling.StartFalling(new List<GroupBlock> { new(5, 40, 5, S), new(6, 40, 5, S) });
        for (var i = 0; i < 600 && falling.Groups.Count > 0; i++) falling.Tick(1f / 60);

        Assert.True(world.IsSolid(5, 11, 5));
        Assert.True(world.IsSolid(6, 11, 5));
        Assert.False(world.IsSolid(5, 10, 5));
    }

    [Fact]
    public void Falling_IsDeterministic()
    {
        float Simulate()
        {
            var world = TShape();
            var falling = new FallingBlocks(world);
            world.SetBlock(10, 5, 10, 0);
            falling.CheckAfterRemoval(10, 5, 10);
            for (var i = 0; i < 7; i++) falling.Tick(1f / 60);
            return falling.Groups[0].Fallen;
        }

        Assert.Equal(BitConverter.SingleToInt32Bits(Simulate()), BitConverter.SingleToInt32Bits(Simulate()));
    }
}
