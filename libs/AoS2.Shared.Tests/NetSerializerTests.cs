using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Tests;

public class NetSerializerTests
{
    private static NetReader ReaderFor(NetWriter w) => new(w.ToArray());

    [Fact]
    public void Primitives_RoundTrip()
    {
        var w = new NetWriter(1);
        w.WriteByte(0xAB);
        w.WriteBool(true);
        w.WriteBool(false);
        w.WriteUShort(0xBEEF);
        w.WriteShort(-12345);
        w.WriteUInt(0xDEADBEEF);
        w.WriteInt(int.MinValue);
        w.WriteFloat(-0f);
        w.WriteFloat(3.14159265f);
        w.WriteFloat(float.Epsilon);
        w.WriteString("Grüße 🙂");
        w.WriteString("");

        var r = ReaderFor(w);
        Assert.Equal(0xAB, r.ReadByte());
        Assert.True(r.ReadBool());
        Assert.False(r.ReadBool());
        Assert.Equal(0xBEEF, r.ReadUShort());
        Assert.Equal(-12345, r.ReadShort());
        Assert.Equal(0xDEADBEEF, r.ReadUInt());
        Assert.Equal(int.MinValue, r.ReadInt());
        Assert.Equal(BitConverter.SingleToInt32Bits(-0f), BitConverter.SingleToInt32Bits(r.ReadFloat())); // bitgenau, auch -0
        Assert.Equal(3.14159265f, r.ReadFloat());
        Assert.Equal(float.Epsilon, r.ReadFloat());
        Assert.Equal("Grüße 🙂", r.ReadString(64));
        Assert.Equal("", r.ReadString(64));
        Assert.True(r.AtEnd);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(127u, 1)]
    [InlineData(128u, 2)]
    [InlineData(16383u, 2)]
    [InlineData(16384u, 3)]
    [InlineData(uint.MaxValue, 5)]
    public void VarUInt_RoundTrip_WithExpectedLength(uint value, int bytes)
    {
        var w = new NetWriter();
        w.WriteVarUInt(value);
        Assert.Equal(bytes, w.Length);
        Assert.Equal(value, ReaderFor(w).ReadVarUInt());
    }

    [Fact]
    public void Writer_GrowsBeyondCapacity()
    {
        var w = new NetWriter(16);
        for (var i = 0; i < 10_000; i++) w.WriteInt(i);
        var r = ReaderFor(w);
        for (var i = 0; i < 10_000; i++) Assert.Equal(i, r.ReadInt());
    }

    [Fact]
    public void Reader_TooShort_Throws()
    {
        Assert.Throws<ProtocolException>(() => new NetReader([1, 2, 3]).ReadUInt());
        Assert.Throws<ProtocolException>(() => new NetReader([]).ReadByte());
    }

    [Fact]
    public void Reader_RespectsOffsetAndCount()
    {
        var r = new NetReader([9, 1, 2, 9], 1, 2);
        Assert.Equal(0x0201, r.ReadUShort());
        Assert.Throws<ProtocolException>(() => r.ReadByte());
    }

    [Fact]
    public void Reader_RejectsInvalidValues()
    {
        Assert.Throws<ProtocolException>(() => new NetReader([2]).ReadBool());
        Assert.Throws<ProtocolException>(() => new NetReader([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01]).ReadVarUInt());
        Assert.Throws<ProtocolException>(() => new NetReader([0xFF, 0xFF, 0xFF, 0xFF, 0x1F]).ReadVarUInt()); // > 32 Bit
        Assert.Throws<ProtocolException>(() => new NetReader([2, 0xC3, 0x28]).ReadString(64)); // kein UTF-8
        Assert.Throws<ProtocolException>(() => new NetReader([100]).ReadString(64)); // länger als erlaubt
        Assert.Throws<ProtocolException>(() => new NetReader([5, 1, 2]).ReadString(64)); // länger als die Daten

        var nan = new NetWriter();
        nan.WriteFloat(float.NaN);
        nan.WriteFloat(float.PositiveInfinity);
        var r = ReaderFor(nan);
        Assert.Throws<ProtocolException>(() => r.ReadFiniteFloat());
        Assert.Throws<ProtocolException>(() => r.ReadFiniteFloat());
    }

    [Fact]
    public void ConnectRequest_RoundTrip_AndWrongMagic()
    {
        var w = new NetWriter();
        new ConnectRequest { ProtocolVersion = ProtocolInfo.Version, Ticket = "abc.def.ghi" }.Write(w);
        var read = ConnectRequest.Read(ReaderFor(w));
        Assert.Equal(ProtocolInfo.Version, read.ProtocolVersion);
        Assert.Equal("abc.def.ghi", read.Ticket);

        var other = new NetWriter();
        other.WriteUInt(0x12345678);
        other.WriteInt(1);
        Assert.Throws<ProtocolException>(() => ConnectRequest.Read(ReaderFor(other)));
    }

    [Fact]
    public void ConnectRequest_OtherVersion_StopsReading()
    {
        // Ein künftiges Protokoll darf anders weitergehen; der Server soll trotzdem „falsche Version“ melden können.
        var w = new NetWriter();
        w.WriteUInt(ConnectRequest.Magic);
        w.WriteInt(ProtocolInfo.Version + 1);
        w.WriteBytes([1, 2, 3, 4, 5], 0, 5);
        var read = ConnectRequest.Read(ReaderFor(w));
        Assert.Equal(ProtocolInfo.Version + 1, read.ProtocolVersion);
        Assert.Null(read.Ticket);
    }

    [Fact]
    public void DisconnectNotice_RoundTrip_AndGarbage()
    {
        var bytes = new DisconnectNotice(DisconnectReason.ServerFull, "Der Server ist voll.").ToBytes();
        var read = DisconnectNotice.Parse(bytes);
        Assert.Equal(DisconnectReason.ServerFull, read.Reason);
        Assert.Equal("Der Server ist voll.", read.Message);

        Assert.Equal(DisconnectReason.Unknown, DisconnectNotice.Parse(null).Reason);
        Assert.Equal(DisconnectReason.Unknown, DisconnectNotice.Parse([4, 200]).Reason);
    }

    [Fact]
    public void InputPacket_RoundTrip_BitExact()
    {
        var packet = new InputPacket { NewestTick = 1000 };
        packet.Inputs.Add(new PlayerInput { Buttons = InputButtons.Forward | InputButtons.Sprint, Yaw = 123.456f, Pitch = -45.5f });
        packet.Inputs.Add(new PlayerInput { Buttons = InputButtons.Jump | InputButtons.Crouch | InputButtons.Left, Yaw = 359.99f, Pitch = 89f });
        var w = new NetWriter();
        packet.Write(w);

        var r = ReaderFor(w);
        Assert.Equal(MessageType.Input, (MessageType)r.ReadByte());
        var read = InputPacket.Read(r);
        Assert.Equal(1000u, read.NewestTick);
        Assert.Equal(2, read.Inputs.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(packet.Inputs[i].Buttons, read.Inputs[i].Buttons);
            Assert.Equal(packet.Inputs[i].Yaw, read.Inputs[i].Yaw);
            Assert.Equal(packet.Inputs[i].Pitch, read.Inputs[i].Pitch);
        }
    }

    [Theory]
    [InlineData(0, 10u)]      // keine Eingabe
    [InlineData(17, 100u)]    // zu viele
    [InlineData(5, 3u)]       // Tick 0 oder kleiner
    public void InputPacket_InvalidCount_Throws(int count, uint newest)
    {
        var w = new NetWriter();
        w.WriteUInt(newest);
        w.WriteByte((byte)count);
        for (var i = 0; i < count; i++) NetCodec.WriteInput(w, default);
        Assert.Throws<ProtocolException>(() => InputPacket.Read(ReaderFor(w)));
    }

    [Fact]
    public void Input_UnknownButtonsOrBadAngles_Throw()
    {
        var w = new NetWriter();
        w.WriteUShort(0x8000);
        w.WriteFloat(0);
        w.WriteFloat(0);
        Assert.Throws<ProtocolException>(() => NetCodec.ReadInput(ReaderFor(w)));

        var pitch = new NetWriter();
        NetCodec.WriteInput(pitch, new PlayerInput { Pitch = 91 });
        Assert.Throws<ProtocolException>(() => NetCodec.ReadInput(ReaderFor(pitch)));
    }

    [Fact]
    public void BlockEditRequest_RoundTrip()
    {
        var e = new BlockEditRequest { Seq = 65535, Tick = 77, Kind = BlockEditKind.Place, X = 511, Y = 63, Z = 4095, BlockId = 9 };
        var w = new NetWriter();
        e.Write(w);
        var r = ReaderFor(w);
        Assert.Equal(MessageType.BlockEdit, (MessageType)r.ReadByte());
        var read = BlockEditRequest.Read(r);
        Assert.Equal(e.Seq, read.Seq);
        Assert.Equal(e.Tick, read.Tick);
        Assert.Equal(e.Kind, read.Kind);
        Assert.Equal((e.X, e.Y, e.Z, e.BlockId), (read.X, read.Y, read.Z, read.BlockId));
    }

    [Fact]
    public void Welcome_RoundTrip()
    {
        var checksum = new byte[32];
        for (var i = 0; i < 32; i++) checksum[i] = (byte)(i * 7);
        var m = new Welcome
        {
            PlayerId = 3,
            PlayerName = "Jan",
            ServerTick = 123456,
            TickRate = 60,
            SnapshotInterval = 2,
            MapName = "testmap",
            MapChecksum = checksum,
            Spawn = new PlayerState { Position = new Vec3(64.5f, 15, 14.5f), Velocity = new Vec3(0, -1, 0), OnGround = true },
        };
        var w = new NetWriter();
        m.Write(w);
        var r = ReaderFor(w);
        Assert.Equal(MessageType.Welcome, (MessageType)r.ReadByte());
        var read = Welcome.Read(r);
        Assert.Equal((m.PlayerId, m.PlayerName, m.ServerTick, m.TickRate, m.SnapshotInterval, m.MapName),
            (read.PlayerId, read.PlayerName, read.ServerTick, read.TickRate, read.SnapshotInterval, read.MapName));
        Assert.Equal(checksum, read.MapChecksum);
        Assert.Equal(m.Spawn.Position, read.Spawn.Position);
        Assert.Equal(m.Spawn.Velocity, read.Spawn.Velocity);
        Assert.True(read.Spawn.OnGround);
        Assert.False(read.Spawn.Crouching);
    }

    [Fact]
    public void Snapshot_RoundTrip_OwnStateExact_OthersRounded()
    {
        var s = new Snapshot
        {
            ServerTick = 999,
            AckInputTick = 500,
            Own = new PlayerState { Position = new Vec3(1.2345678f, 2.5f, 3.75f), Velocity = new Vec3(0.1f, -9.8f, 4.5f), Crouching = true },
        };
        s.Players.Add(new PlayerSnapshot { Id = 7, Position = new Vec3(10, 20, 30), Yaw = 271.3f, Pitch = -33.3f, OnGround = true });
        s.Players.Add(new PlayerSnapshot { Id = 8, Position = new Vec3(-1, 0, 0.5f), Yaw = -10f, Pitch = 89.9f, Crouching = true });
        s.Falling.Add(new FallingSnapshot { GroupId = 1234, Fallen = 2.75f });
        var w = new NetWriter();
        s.Write(w);
        Assert.True(w.Length <= Snapshot.HeaderBytes + 2 * Snapshot.MaxPlayerBytes + 9, $"Snapshot mit 2 Spielern ist {w.Length} Byte groß");

        var r = ReaderFor(w);
        Assert.Equal(MessageType.Snapshot, (MessageType)r.ReadByte());
        var read = Snapshot.Read(r);
        Assert.Equal(999u, read.ServerTick);
        Assert.Equal(500u, read.AckInputTick);
        Assert.Equal(s.Own.Position, read.Own.Position);
        Assert.Equal(s.Own.Velocity, read.Own.Velocity);
        Assert.True(read.Own.Crouching);

        Assert.Equal(2, read.Players.Count);
        Assert.Equal(7, read.Players[0].Id);
        Assert.Equal(new Vec3(10, 20, 30), read.Players[0].Position);
        Assert.Equal(271.3f, read.Players[0].Yaw, 0.01f);
        Assert.Equal(-33.3f, read.Players[0].Pitch, 0.01f);
        Assert.True(read.Players[0].OnGround);
        Assert.Equal(350f, read.Players[1].Yaw, 0.01f); // auf 0 … 360 gebracht
        Assert.True(read.Players[1].Crouching);

        Assert.Single(read.Falling);
        Assert.Equal(1234, read.Falling[0].GroupId);
        Assert.Equal(2.75f, read.Falling[0].Fallen);
    }

    [Fact]
    public void WorldEvents_AllTypes_RoundTrip()
    {
        var blocks = new List<GroupBlock> { new(40, 10, 40, 9), new(41, 10, 40, 9), new(40, 11, 300, 2) };
        var w = new NetWriter();
        WorldEvents.WriteHeader(w);
        WorldEvents.WriteBlockSet(w, 1, 2, 3, 4);
        WorldEvents.WriteEditResult(w, 42, BlockEditResult.NoLineOfSight);
        WorldEvents.WriteFallStart(w, 5, blocks, 1.5f, 3.25f);
        WorldEvents.WriteFallLand(w, 5, 7);
        WorldEvents.WritePlayerJoined(w, 2, "Ännchen");
        WorldEvents.WritePlayerLeft(w, 2);
        WorldEvents.WriteSyncComplete(w);

        var r = ReaderFor(w);
        Assert.Equal(MessageType.Events, (MessageType)r.ReadByte());
        var events = new List<WorldEvent>();
        WorldEvents.Read(r, events);

        Assert.Equal(
            [WorldEventType.BlockSet, WorldEventType.EditResult, WorldEventType.FallStart, WorldEventType.FallLand,
             WorldEventType.PlayerJoined, WorldEventType.PlayerLeft, WorldEventType.SyncComplete],
            events.Select(e => e.Type));
        Assert.Equal((1, 2, 3, (byte)4), (events[0].X, events[0].Y, events[0].Z, events[0].BlockId));
        Assert.Equal((42, BlockEditResult.NoLineOfSight), (events[1].Seq, events[1].Result));
        Assert.Equal(5, events[2].GroupId);
        Assert.Equal(1.5f, events[2].Fallen);
        Assert.Equal(3.25f, events[2].Velocity);
        Assert.Equal(blocks, events[2].Blocks);
        Assert.Equal((5, 7), (events[3].GroupId, events[3].Offset));
        Assert.Equal((2, "Ännchen"), (events[4].PlayerId, events[4].Name));
        Assert.Equal(2, events[5].PlayerId);
    }

    [Fact]
    public void WorldEvents_LargeGroup_IsCompact()
    {
        var blocks = new List<GroupBlock>();
        for (var x = 0; x < 20; x++)
            for (var y = 0; y < 20; y++)
                for (var z = 0; z < 20; z++)
                    blocks.Add(new GroupBlock(100 + x, 30 + y, 200 + z, 2));
        var w = new NetWriter();
        WorldEvents.WriteFallStart(w, 1, blocks, 0, 0);
        Assert.True(w.Length < blocks.Count * 5, $"{blocks.Count} Blöcke brauchen {w.Length} Byte");
    }

    [Fact]
    public void WorldEvents_UnknownType_Throws()
    {
        var events = new List<WorldEvent>();
        Assert.Throws<ProtocolException>(() => WorldEvents.Read(new NetReader([99]), events));
    }

    /// <summary>Daten aus dem Netz können alles sein: Jeder Leser darf nur ProtocolException werfen, nie etwas anderes.</summary>
    [Fact]
    public void RandomBytes_OnlyEverThrowProtocolException()
    {
        var random = new Random(1234);
        var readers = new Action<NetReader>[]
        {
            r => ConnectRequest.Read(r),
            r => InputPacket.Read(r),
            r => BlockEditRequest.Read(r),
            r => Welcome.Read(r),
            r => Snapshot.Read(r),
            r => WorldEvents.Read(r, new List<WorldEvent>()),
        };
        for (var i = 0; i < 20_000; i++)
        {
            var data = new byte[random.Next(0, 80)];
            random.NextBytes(data);
            var read = readers[i % readers.Length];
            try
            {
                read(new NetReader(data));
            }
            catch (ProtocolException)
            {
            }
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(359.99f)]
    [InlineData(-90f)]
    [InlineData(720f)]
    public void Yaw_Quantization_IsAccurate(float yaw)
    {
        var back = NetCodec.DequantizeYaw(NetCodec.QuantizeYaw(yaw));
        var diff = Math.Abs(((back - yaw) % 360 + 540) % 360 - 180);
        Assert.True(diff < 0.01f, $"{yaw} → {back}");
    }
}
