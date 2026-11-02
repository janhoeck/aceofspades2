using AoS2.Shared;

namespace AoS2.Shared.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void Version_IsPositive()
    {
        Assert.True(ProtocolInfo.Version > 0);
    }

    [Fact]
    public void IsCompatible_SameVersion_ReturnsTrue()
    {
        Assert.True(ProtocolInfo.IsCompatible(ProtocolInfo.Version));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProtocolInfo.Version + 1)]
    public void IsCompatible_OtherVersion_ReturnsFalse(int otherVersion)
    {
        Assert.False(ProtocolInfo.IsCompatible(otherVersion));
    }
}
