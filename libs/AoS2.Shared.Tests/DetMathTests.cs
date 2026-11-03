namespace AoS2.Shared.Tests;

public class DetMathTests
{
    [Fact]
    public void SinCos_MatchSystemMath()
    {
        for (var deg = -1000f; deg <= 1000f; deg += 0.37f)
        {
            Assert.Equal(Math.Sin(deg * Math.PI / 180), DetMath.SinDeg(deg), 1e-5);
            Assert.Equal(Math.Cos(deg * Math.PI / 180), DetMath.CosDeg(deg), 1e-5);
        }
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(90f, 1f)]
    [InlineData(-90f, -1f)]
    [InlineData(180f, 0f)]
    [InlineData(270f, -1f)]
    public void SinDeg_KeyAngles(float degrees, float expected)
    {
        Assert.Equal(expected, DetMath.SinDeg(degrees), 5);
    }

    [Theory]
    [InlineData(1.5f, 1)]
    [InlineData(-0.5f, -1)]
    [InlineData(-2f, -2)]
    [InlineData(3f, 3)]
    public void FloorToInt(float value, int expected)
    {
        Assert.Equal(expected, DetMath.FloorToInt(value));
    }
}
