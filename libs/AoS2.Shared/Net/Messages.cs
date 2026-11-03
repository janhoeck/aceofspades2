using System.Collections.Generic;
using AoS2.Shared.Movement;
using AoS2.Shared.Voxels;

namespace AoS2.Shared.Net
{
    // Jede Nachricht hat ein Write und ein Read, die Feld für Feld spiegelbildlich sind.
    // Read prüft alle Werte, die aus dem Netz kommen, und wirft sonst ProtocolException.

    /// <summary>Daten der Verbindungsanfrage (Client → Server, noch vor dem ersten Paket).</summary>
    public struct ConnectRequest
    {
        /// <summary>Kennung „AOS2“, damit fremde Programme auf demselben Port sofort abgelehnt werden.</summary>
        public const uint Magic = 0x32534F41;

        public int ProtocolVersion;

        /// <summary>Signiertes Join-Ticket (JWT) vom Backend oder ein Entwicklungs-Ticket (<see cref="DevJoinTicket"/>).</summary>
        public string Ticket;

        public void Write(NetWriter w)
        {
            w.WriteUInt(Magic);
            w.WriteInt(ProtocolVersion);
            w.WriteString(Ticket);
        }

        public static ConnectRequest Read(NetReader r)
        {
            if (r.ReadUInt() != Magic) throw new ProtocolException("Keine Ace-of-Spades-2-Verbindung.");
            var request = new ConnectRequest { ProtocolVersion = r.ReadInt() };
            // Bei falscher Protokollversion kann der Rest anders aussehen; dann nicht weiterlesen.
            if (!ProtocolInfo.IsCompatible(request.ProtocolVersion)) return request;
            request.Ticket = r.ReadString(NetLimits.MaxTicketBytes);
            r.ExpectEnd();
            return request;
        }
    }

    /// <summary>Begründung beim Ablehnen oder Trennen einer Verbindung (Server → Client).</summary>
    public struct DisconnectNotice
    {
        public DisconnectReason Reason;
        public string Message;

        public DisconnectNotice(DisconnectReason reason, string message)
        {
            Reason = reason;
            Message = message;
        }

        public byte[] ToBytes()
        {
            var w = new NetWriter(64);
            w.WriteByte((byte)Reason);
            w.WriteString(Message);
            return w.ToArray();
        }

        /// <summary>Liest eine Begründung; unlesbare Daten ergeben <see cref="DisconnectReason.Unknown"/> statt eines Fehlers.</summary>
        public static DisconnectNotice Parse(byte[] data)
        {
            if (data == null || data.Length == 0) return new DisconnectNotice(DisconnectReason.Unknown, "");
            try
            {
                var r = new NetReader(data);
                return new DisconnectNotice((DisconnectReason)r.ReadByte(), r.ReadString(NetLimits.MaxMessageBytes));
            }
            catch (ProtocolException)
            {
                return new DisconnectNotice(DisconnectReason.Unknown, "");
            }
        }
    }

    /// <summary>
    /// Eingaben des Clients (unzuverlässig). Enthält die neueste Eingabe und die davor, die der Server noch nicht
    /// bestätigt hat: Geht ein Paket verloren, bringt das nächste die fehlenden Eingaben mit.
    /// </summary>
    public sealed class InputPacket
    {
        /// <summary>Tick der letzten Eingabe in <see cref="Inputs"/>; die erste hat <c>NewestTick - Count + 1</c>.</summary>
        public uint NewestTick;

        public readonly List<PlayerInput> Inputs = new List<PlayerInput>();

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.Input);
            w.WriteUInt(NewestTick);
            w.WriteByte((byte)Inputs.Count);
            foreach (var input in Inputs) NetCodec.WriteInput(w, input);
        }

        /// <summary>Liest den Inhalt nach dem Typ-Byte.</summary>
        public static InputPacket Read(NetReader r)
        {
            var packet = new InputPacket { NewestTick = r.ReadUInt() };
            int count = r.ReadByte();
            if (count < 1 || count > NetLimits.MaxInputsPerPacket || count > packet.NewestTick)
                throw new ProtocolException($"Ungültige Zahl von Eingaben ({count}).");
            for (var i = 0; i < count; i++) packet.Inputs.Add(NetCodec.ReadInput(r));
            r.ExpectEnd();
            return packet;
        }
    }

    public enum BlockEditKind : byte
    {
        Remove = 0,
        Place = 1,
    }

    /// <summary>
    /// Absicht, einen Block abzubauen oder zu bauen (zuverlässig). <see cref="Tick"/> ist der Eingabe-Tick,
    /// nach dessen Bewegung der Client die Aktion ausgelöst hat: Der Server prüft sie mit genau dem Zustand von damals.
    /// </summary>
    public struct BlockEditRequest
    {
        public ushort Seq;
        public uint Tick;
        public BlockEditKind Kind;
        public int X, Y, Z;

        /// <summary>Blocktyp beim Bauen (beim Abbauen 0).</summary>
        public byte BlockId;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.BlockEdit);
            w.WriteUShort(Seq);
            w.WriteUInt(Tick);
            w.WriteByte((byte)Kind);
            NetCodec.WriteBlockPos(w, X, Y, Z);
            w.WriteByte(BlockId);
        }

        public static BlockEditRequest Read(NetReader r)
        {
            var e = new BlockEditRequest { Seq = r.ReadUShort(), Tick = r.ReadUInt() };
            var kind = r.ReadByte();
            if (kind > (byte)BlockEditKind.Place) throw new ProtocolException($"Unbekannte Blockaktion {kind}.");
            e.Kind = (BlockEditKind)kind;
            NetCodec.ReadBlockPos(r, out e.X, out e.Y, out e.Z);
            e.BlockId = r.ReadByte();
            r.ExpectEnd();
            return e;
        }
    }

    /// <summary>Erste Nachricht nach dem Beitritt (zuverlässig): wer man ist, welche Map läuft, wo man steht.</summary>
    public sealed class Welcome
    {
        public byte PlayerId;
        public string PlayerName = "";
        public uint ServerTick;
        public byte TickRate;

        /// <summary>Alle wie viele Ticks ein Snapshot kommt (2 = 30 Snapshots/s bei 60 Ticks/s).</summary>
        public byte SnapshotInterval;

        /// <summary>Dateiname der Map ohne Endung (<c>maps/&lt;name&gt;.aos2map</c>).</summary>
        public string MapName = "";

        /// <summary>SHA-256 der Map-Datei (<see cref="MapChecksum"/>). Der Client prüft damit, ob er dieselbe Map hat.</summary>
        public byte[] MapChecksum = new byte[NetLimits.ChecksumBytes];

        public PlayerState Spawn;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.Welcome);
            w.WriteByte(PlayerId);
            w.WriteString(PlayerName);
            w.WriteUInt(ServerTick);
            w.WriteByte(TickRate);
            w.WriteByte(SnapshotInterval);
            w.WriteString(MapName);
            w.WriteBytes(MapChecksum, 0, NetLimits.ChecksumBytes);
            NetCodec.WriteState(w, Spawn);
        }

        public static Welcome Read(NetReader r)
        {
            var m = new Welcome
            {
                PlayerId = r.ReadByte(),
                PlayerName = r.ReadString(NetLimits.MaxNameBytes),
                ServerTick = r.ReadUInt(),
                TickRate = r.ReadByte(),
                SnapshotInterval = r.ReadByte(),
                MapName = r.ReadString(NetLimits.MaxMapNameBytes),
                MapChecksum = r.ReadBytes(NetLimits.ChecksumBytes),
                Spawn = NetCodec.ReadState(r),
            };
            if (m.TickRate == 0 || m.SnapshotInterval == 0) throw new ProtocolException("Ungültige Tickrate.");
            r.ExpectEnd();
            return m;
        }
    }

    /// <summary>Ein anderer Spieler im Snapshot (gerundet, nur für die Anzeige).</summary>
    public struct PlayerSnapshot
    {
        public byte Id;
        public Vec3 Position;
        public float Yaw;
        public float Pitch;
        public bool Crouching;
        public bool OnGround;
    }

    /// <summary>Wie weit eine fallende Gruppe gerade gefallen ist.</summary>
    public struct FallingSnapshot
    {
        public int GroupId;
        public float Fallen;
    }

    /// <summary>
    /// Zustand der Welt zu einem Server-Tick (unzuverlässig, ca. 30/s). Enthält den eigenen Spieler in voller Genauigkeit
    /// samt dem letzten Eingabe-Tick, den der Server verarbeitet hat (für den Abgleich), und alle anderen gerundet.
    /// </summary>
    public sealed class Snapshot
    {
        public uint ServerTick;

        /// <summary>Letzte verarbeitete Eingabe des Empfängers. <see cref="Own"/> ist der Zustand direkt danach.</summary>
        public uint AckInputTick;

        public PlayerState Own;
        public readonly List<PlayerSnapshot> Players = new List<PlayerSnapshot>();
        public readonly List<FallingSnapshot> Falling = new List<FallingSnapshot>();

        private const byte FlagOnGround = 1;
        private const byte FlagCrouching = 2;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.Snapshot);
            w.WriteUInt(ServerTick);
            w.WriteUInt(AckInputTick);
            NetCodec.WriteState(w, Own);
            w.WriteByte((byte)Players.Count);
            foreach (var p in Players)
            {
                w.WriteByte(p.Id);
                NetCodec.WriteVec(w, p.Position);
                w.WriteUShort(NetCodec.QuantizeYaw(p.Yaw));
                w.WriteShort(NetCodec.QuantizePitch(p.Pitch));
                w.WriteByte((byte)((p.OnGround ? FlagOnGround : 0) | (p.Crouching ? FlagCrouching : 0)));
            }
            w.WriteByte((byte)Falling.Count);
            foreach (var f in Falling)
            {
                w.WriteVarUInt((uint)f.GroupId);
                w.WriteFloat(f.Fallen);
            }
        }

        public static Snapshot Read(NetReader r)
        {
            var s = new Snapshot { ServerTick = r.ReadUInt(), AckInputTick = r.ReadUInt(), Own = NetCodec.ReadState(r) };
            int players = r.ReadByte();
            if (players > NetLimits.MaxPlayers) throw new ProtocolException("Zu viele Spieler im Snapshot.");
            for (var i = 0; i < players; i++)
            {
                var p = new PlayerSnapshot
                {
                    Id = r.ReadByte(),
                    Position = NetCodec.ReadVec(r),
                    Yaw = NetCodec.DequantizeYaw(r.ReadUShort()),
                    Pitch = NetCodec.DequantizePitch(r.ReadShort()),
                };
                var flags = r.ReadByte();
                p.OnGround = (flags & FlagOnGround) != 0;
                p.Crouching = (flags & FlagCrouching) != 0;
                s.Players.Add(p);
            }
            int groups = r.ReadByte();
            for (var i = 0; i < groups; i++)
                s.Falling.Add(new FallingSnapshot { GroupId = (int)r.ReadVarUInt(), Fallen = r.ReadFiniteFloat() });
            r.ExpectEnd();
            return s;
        }
    }

    public enum WorldEventType : byte
    {
        /// <summary>Ein Block wurde gesetzt oder entfernt (Abbauen, Bauen, später Explosionen).</summary>
        BlockSet = 1,

        /// <summary>Antwort auf eine eigene <see cref="BlockEditRequest"/> (nur an den Absender).</summary>
        EditResult = 2,

        /// <summary>Eine Blockgruppe beginnt zu fallen: Ihre Blöcke verschwinden aus der Welt.</summary>
        FallStart = 3,

        /// <summary>Eine Gruppe ist aufgeschlagen und steht um <see cref="WorldEvent.Offset"/> Blöcke tiefer wieder in der Welt.</summary>
        FallLand = 4,

        PlayerJoined = 5,
        PlayerLeft = 6,

        /// <summary>Ende der Welt-Synchronisation beim Beitritt: Ab jetzt ist die Welt des Clients vollständig.</summary>
        SyncComplete = 7,
    }

    /// <summary>
    /// Ein Ereignis aus einer <see cref="MessageType.Events"/>-Nachricht (zuverlässig, geordnet).
    /// Welche Felder gelten, hängt von <see cref="Type"/> ab.
    /// </summary>
    public struct WorldEvent
    {
        public WorldEventType Type;
        public int X, Y, Z;
        public byte BlockId;
        public ushort Seq;
        public BlockEditResult Result;
        public int GroupId;
        public List<GroupBlock> Blocks;
        public float Fallen;
        public float Velocity;
        public int Offset;
        public byte PlayerId;
        public string Name;
    }

    /// <summary>
    /// Ereignisse werden hintereinander in eine Nachricht geschrieben (Typ-Byte + Inhalt), bis sie voll ist.
    /// Eine <see cref="MessageType.Events"/>-Nachricht ist also: Typ-Byte, dann beliebig viele Ereignisse bis zum Ende.
    /// </summary>
    public static class WorldEvents
    {
        public static void WriteHeader(NetWriter w) => w.WriteByte((byte)MessageType.Events);

        public static void WriteBlockSet(NetWriter w, int x, int y, int z, byte id)
        {
            w.WriteByte((byte)WorldEventType.BlockSet);
            NetCodec.WriteBlockPos(w, x, y, z);
            w.WriteByte(id);
        }

        public static void WriteEditResult(NetWriter w, ushort seq, BlockEditResult result)
        {
            w.WriteByte((byte)WorldEventType.EditResult);
            w.WriteUShort(seq);
            w.WriteByte((byte)result);
        }

        /// <summary>Blöcke relativ zur kleinsten Ecke der Gruppe (Varints), damit große Gruppen klein bleiben.</summary>
        public static void WriteFallStart(NetWriter w, int groupId, IReadOnlyList<GroupBlock> blocks, float fallen, float velocity)
        {
            w.WriteByte((byte)WorldEventType.FallStart);
            w.WriteVarUInt((uint)groupId);
            w.WriteFloat(fallen);
            w.WriteFloat(velocity);
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            foreach (var b in blocks)
            {
                if (b.X < minX) minX = b.X;
                if (b.Y < minY) minY = b.Y;
                if (b.Z < minZ) minZ = b.Z;
            }
            NetCodec.WriteBlockPos(w, minX, minY, minZ);
            w.WriteVarUInt((uint)blocks.Count);
            foreach (var b in blocks)
            {
                w.WriteVarUInt((uint)(b.X - minX));
                w.WriteVarUInt((uint)(b.Y - minY));
                w.WriteVarUInt((uint)(b.Z - minZ));
                w.WriteByte(b.Id);
            }
        }

        public static void WriteFallLand(NetWriter w, int groupId, int offset)
        {
            w.WriteByte((byte)WorldEventType.FallLand);
            w.WriteVarUInt((uint)groupId);
            w.WriteVarUInt((uint)offset);
        }

        public static void WritePlayerJoined(NetWriter w, byte playerId, string name)
        {
            w.WriteByte((byte)WorldEventType.PlayerJoined);
            w.WriteByte(playerId);
            w.WriteString(name);
        }

        public static void WritePlayerLeft(NetWriter w, byte playerId)
        {
            w.WriteByte((byte)WorldEventType.PlayerLeft);
            w.WriteByte(playerId);
        }

        public static void WriteSyncComplete(NetWriter w) => w.WriteByte((byte)WorldEventType.SyncComplete);

        /// <summary>Liest alle Ereignisse nach dem Typ-Byte bis zum Ende der Nachricht.</summary>
        public static void Read(NetReader r, List<WorldEvent> into)
        {
            while (!r.AtEnd)
            {
                var e = new WorldEvent { Type = (WorldEventType)r.ReadByte() };
                switch (e.Type)
                {
                    case WorldEventType.BlockSet:
                        NetCodec.ReadBlockPos(r, out e.X, out e.Y, out e.Z);
                        e.BlockId = r.ReadByte();
                        break;
                    case WorldEventType.EditResult:
                        e.Seq = r.ReadUShort();
                        e.Result = (BlockEditResult)r.ReadByte();
                        break;
                    case WorldEventType.FallStart:
                        e.GroupId = (int)r.ReadVarUInt();
                        e.Fallen = r.ReadFiniteFloat();
                        e.Velocity = r.ReadFiniteFloat();
                        NetCodec.ReadBlockPos(r, out var minX, out var minY, out var minZ);
                        var count = r.ReadCount(NetLimits.MaxGroupBlocks);
                        e.Blocks = new List<GroupBlock>(count);
                        for (var i = 0; i < count; i++)
                        {
                            var x = minX + r.ReadCount(NetLimits.MaxCoordinate);
                            var y = minY + r.ReadCount(NetLimits.MaxCoordinate);
                            var z = minZ + r.ReadCount(NetLimits.MaxCoordinate);
                            e.Blocks.Add(new GroupBlock(x, y, z, r.ReadByte()));
                        }
                        break;
                    case WorldEventType.FallLand:
                        e.GroupId = (int)r.ReadVarUInt();
                        e.Offset = r.ReadCount(NetLimits.MaxCoordinate);
                        break;
                    case WorldEventType.PlayerJoined:
                        e.PlayerId = r.ReadByte();
                        e.Name = r.ReadString(NetLimits.MaxNameBytes);
                        break;
                    case WorldEventType.PlayerLeft:
                        e.PlayerId = r.ReadByte();
                        break;
                    case WorldEventType.SyncComplete:
                        break;
                    default:
                        throw new ProtocolException($"Unbekanntes Ereignis {(byte)e.Type}.");
                }
                into.Add(e);
            }
        }
    }
}
