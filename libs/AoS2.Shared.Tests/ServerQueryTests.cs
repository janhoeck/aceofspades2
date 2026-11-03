using AoS2.Shared.Net;

namespace AoS2.Shared.Tests;

public class ServerQueryTests
{
    [Fact]
    public void Query_HasFixedSize_AndRoundTrips()
    {
        var challenge = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var plain = new ServerQuery { Nonce = 7 }.ToBytes();
        var withChallenge = new ServerQuery { Nonce = 0xDEADBEEF, Challenge = challenge }.ToBytes();

        Assert.Equal(ServerQuery.Size, plain.Length);
        Assert.Equal(ServerQuery.Size, withChallenge.Length);
        var a = ServerQuery.Read(plain, 0, plain.Length);
        Assert.Equal(7u, a.Nonce);
        Assert.Null(a.Challenge);
        var b = ServerQuery.Read(withChallenge, 0, withChallenge.Length);
        Assert.Equal(0xDEADBEEF, b.Nonce);
        Assert.Equal(challenge, b.Challenge);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(79)]
    [InlineData(81)]
    public void Query_WithWrongSize_IsRejected(int size)
    {
        var data = new byte[size];
        var valid = new ServerQuery { Nonce = 1 }.ToBytes();
        Array.Copy(valid, data, Math.Min(size, valid.Length));

        Assert.Throws<ProtocolException>(() => ServerQuery.Read(data, 0, data.Length));
    }

    [Fact]
    public void Query_WithOtherMagic_IsRejected()
    {
        var data = new ServerQuery { Nonce = 1 }.ToBytes();
        data[0] ^= 0xFF;

        Assert.Throws<ProtocolException>(() => ServerQuery.Read(data, 0, data.Length));
    }

    [Fact]
    public void Info_RoundTrips()
    {
        var proof = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        var info = new ServerInfo
        {
            Nonce = 42, ProtocolVersion = ProtocolInfo.Version, Proof = proof,
            Name = "Jans Grüne Hölle 🙂", Map = "testmap", Players = 12, MaxPlayers = 32,
        };

        var bytes = info.ToBytes();
        var read = ServerInfo.Read(bytes, 0, bytes.Length);

        Assert.Equal(42u, read.Nonce);
        Assert.True(read.IsCompatible);
        Assert.Equal(proof, read.Proof);
        Assert.Equal("Jans Grüne Hölle 🙂", read.Name);
        Assert.Equal("testmap", read.Map);
        Assert.Equal(12, read.Players);
        Assert.Equal(32, read.MaxPlayers);
    }

    [Fact]
    public void Info_IsAtMostThreeTimesTheQuery()
    {
        var longest = new ServerInfo
        {
            ProtocolVersion = ProtocolInfo.Version, Proof = new byte[32],
            Name = new string('€', ServerListing.MaxNameLength), // 3 Byte je Zeichen in UTF-8
            Map = new string('m', ServerListing.MaxMapLength), Players = 48, MaxPlayers = 48,
        };

        var size = longest.ToBytes().Length;

        Assert.True(size <= ServerQuery.Size * 3, $"Antwort {size} Byte");
    }

    [Fact]
    public void Info_FromOtherProtocolVersion_KeepsStablePrefixOnly()
    {
        // Ein Server mit einer anderen Version kann nach dem Nachweis etwas ganz anderes schicken.
        var w = new NetWriter();
        w.WriteUInt(ServerInfo.Magic);
        w.WriteUInt(5);
        w.WriteInt(ProtocolInfo.Version + 1);
        w.WriteBool(true);
        w.WriteBytes(new byte[32], 0, 32);
        w.WriteUInt(0xFFFFFFFF); // unbekannte Felder
        var bytes = w.ToArray();

        var read = ServerInfo.Read(bytes, 0, bytes.Length);

        Assert.False(read.IsCompatible);
        Assert.Equal(ProtocolInfo.Version + 1, read.ProtocolVersion);
        Assert.NotNull(read.Proof);
        Assert.Null(read.Name);
    }

    [Fact]
    public void Info_WithMorePlayersThanSlots_IsRejected()
    {
        var bytes = new ServerInfo { ProtocolVersion = ProtocolInfo.Version, Name = "x", Map = "m", Players = 9, MaxPlayers = 8 }.ToBytes();

        Assert.Throws<ProtocolException>(() => ServerInfo.Read(bytes, 0, bytes.Length));
    }

    [Fact]
    public void Proof_MatchesOnlyWithSameSecretAndChallenge()
    {
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var challenge = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        var proof = ServerProof.Compute(secret, challenge);

        Assert.True(ServerProof.Matches(secret, challenge, proof));
        Assert.False(ServerProof.Matches(secret.Reverse().ToArray(), challenge, proof));
        Assert.False(ServerProof.Matches(secret, challenge.Reverse().ToArray(), proof));
        Assert.False(ServerProof.Matches(secret, challenge, null));
        Assert.False(ServerProof.Matches(secret, challenge, new byte[31]));
    }

    [Theory]
    [InlineData("Ace of Spades 2", true)]
    [InlineData("Jans Server #1 🙂", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" führendes Leerzeichen", false)]
    [InlineData("Zeile\nUmbruch", false)]
    [InlineData("unsichtbar​es Zeichen", false)]
    [InlineData("1234567890123456789012345678901234567890", true)]
    [InlineData("12345678901234567890123456789012345678901", false)]
    public void ServerName_Rules(string name, bool valid) => Assert.Equal(valid, ServerListing.IsValidName(name));

    [Theory]
    [InlineData("testmap", true)]
    [InlineData("Map_2-final", true)]
    [InlineData("", false)]
    [InlineData("../geheim", false)]
    [InlineData("leer zeichen", false)]
    public void MapName_Rules(string map, bool valid) => Assert.Equal(valid, ServerListing.IsValidMap(map));
}
