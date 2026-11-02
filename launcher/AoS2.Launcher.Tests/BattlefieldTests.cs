using AoS2.Launcher.Controls;

namespace AoS2.Launcher.Tests;

public class BattlefieldTests
{
    [Fact]
    public void Generate_SameSeed_SameBlocks()
    {
        Assert.Equal(BattlefieldGenerator.Generate(60, 20, 7), BattlefieldGenerator.Generate(60, 20, 7));
    }

    [Fact]
    public void Generate_OtherSeed_OtherBlocks()
    {
        Assert.NotEqual(BattlefieldGenerator.Generate(60, 20, 7), BattlefieldGenerator.Generate(60, 20, 23));
    }

    [Theory]
    [InlineData(60, 20)]
    [InlineData(10, 4)]
    [InlineData(1, 1)]
    public void Generate_AllBlocksInsideArea(int columns, int rows)
    {
        var blocks = BattlefieldGenerator.Generate(columns, rows, 7);

        Assert.All(blocks, b => Assert.InRange(b.X, 0, columns - 1));
        Assert.All(blocks, b => Assert.InRange(b.Y, 0, rows - 1));
    }

    [Fact]
    public void Generate_HasBothTeams()
    {
        var blocks = BattlefieldGenerator.Generate(60, 20, 7);

        Assert.Contains(blocks, b => b.Color == Avalonia.Media.Color.Parse("#2F6FE4") && b.X < 30);
        Assert.Contains(blocks, b => b.Color == Avalonia.Media.Color.Parse("#3FA34D") && b.X >= 30);
    }
}
