using AoS2.Backend.Data;

namespace AoS2.Backend.Tests;

public sealed class ConnectionStringTests
{
    [Theory]
    [InlineData("postgres://aos2:geheim@db-x1y2:5432/aos2", "Host=db-x1y2;Port=5432;Database=aos2;Username=aos2;Password=geheim")]
    [InlineData("postgresql://aos2:p%40ss%3Aw@db/aos2", "Host=db;Port=5432;Database=aos2;Username=aos2;Password=p@ss:w")]
    [InlineData("postgres://postgres:pw@db:6543", "Host=db;Port=6543;Database=postgres;Username=postgres;Password=pw")]
    public void Url_IsConverted(string url, string expected) => Assert.Equal(expected, BackendDbOptions.ToNpgsql(url));

    [Fact]
    public void NpgsqlForm_StaysAsIs() =>
        Assert.Equal("Host=127.0.0.1;Port=5433;Database=aos2;Username=aos2", BackendDbOptions.ToNpgsql("Host=127.0.0.1;Port=5433;Database=aos2;Username=aos2"));
}
