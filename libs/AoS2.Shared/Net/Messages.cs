using System.Collections.Generic;
using AoS2.Shared;
using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Rig;
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

    /// <summary>Ein anderer Spieler im Snapshot (Blickwinkel gerundet, nur für die Anzeige und den Lag-Ausgleich).</summary>
    public struct PlayerSnapshot
    {
        public byte Id;
        public Vec3 Position;
        public float Yaw;
        public float Pitch;
        public bool Crouching;
        public bool OnGround;

        /// <summary>Lebt der Spieler? Tote werden nicht gezeigt und nicht getroffen.</summary>
        public bool Alive;

        /// <summary>Zählt die Leben (+1 bei jedem Respawn): Zwischen zwei Leben wird nicht interpoliert, man springt zum Spawn.</summary>
        public byte Life;

        /// <summary>Gewählter Platz (0 = Spaten), bestimmt die Haltung der Arme.</summary>
        public byte Weapon;

        public AnimationState Animation;

        /// <summary>Blickwinkel so gerundet, wie sie im Snapshot ankommen. Der Server merkt sich diese Werte für den Lag-Ausgleich.</summary>
        public PlayerSnapshot Quantized()
        {
            var q = this;
            q.Yaw = NetCodec.DequantizeYaw(NetCodec.QuantizeYaw(Yaw));
            q.Pitch = NetCodec.DequantizePitch(NetCodec.QuantizePitch(Pitch));
            return q;
        }

        /// <summary>
        /// Zwischenwert zweier Snapshots (Interpolation). Client (Anzeige) und Server (Lag-Ausgleich) nutzen genau diese
        /// Rechnung, damit der Server dieselbe Position und Pose prüft, die der Schütze gesehen hat. Ganzzahlige Werte
        /// (Haltung, Animation, Waffe) kommen vom näheren Snapshot; nach einem Respawn wird nicht interpoliert.
        /// </summary>
        public static PlayerSnapshot Lerp(PlayerSnapshot a, PlayerSnapshot b, float t)
        {
            if (a.Life != b.Life) return t >= 1 ? b : a.Alive ? a : b;
            var later = t >= 0.5f ? b : a;
            var yawDelta = b.Yaw - a.Yaw;
            if (yawDelta > 180f) yawDelta -= 360f;
            else if (yawDelta < -180f) yawDelta += 360f;
            later.Position = a.Position + (b.Position - a.Position) * t;
            later.Yaw = a.Yaw + yawDelta * t;
            later.Pitch = a.Pitch + (b.Pitch - a.Pitch) * t;
            return later;
        }
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
        /// <summary>Größe eines anderen Spielers im Snapshot: höchstens so viele Byte (mit laufender Überblendung).</summary>
        public const int MaxPlayerBytes = 1 + 12 + 2 + 2 + 1 + 1 + 1 + 3 + 5;

        /// <summary>Größe ohne andere Spieler und fallende Gruppen.</summary>
        public const int HeaderBytes = 1 + 4 + 4 + 25 + 1 + 2 + 1 + (6 + 4 * CombatSettings.MaxWeapons) + 1 + 1;

        public uint ServerTick;

        /// <summary>Letzte verarbeitete Eingabe des Empfängers. <see cref="Own"/> ist der Zustand direkt danach.</summary>
        public uint AckInputTick;

        public PlayerState Own;

        /// <summary>Waffen des Empfängers nach <see cref="AckInputTick"/> (für den Abgleich der Vorhersage).</summary>
        public WeaponState OwnWeapons;

        /// <summary>Lebenspunkte des Empfängers (0 = tot).</summary>
        public byte OwnHealth;

        /// <summary>Tot: Ticks bis zum Respawn.</summary>
        public ushort OwnRespawnTicks;

        public byte OwnLife;

        public readonly List<PlayerSnapshot> Players = new List<PlayerSnapshot>();
        public readonly List<FallingSnapshot> Falling = new List<FallingSnapshot>();

        private const byte FlagOnGround = 1;
        private const byte FlagCrouching = 2;
        private const byte FlagAlive = 4;
        private const byte FlagBlending = 8;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.Snapshot);
            w.WriteUInt(ServerTick);
            w.WriteUInt(AckInputTick);
            NetCodec.WriteState(w, Own);
            w.WriteByte(OwnHealth);
            w.WriteUShort(OwnRespawnTicks);
            w.WriteByte(OwnLife);
            NetCodec.WriteWeapons(w, OwnWeapons);
            w.WriteByte((byte)Players.Count);
            foreach (var p in Players)
            {
                var blending = p.Animation.IsBlending(ServerTick) && p.Animation.Previous != p.Animation.Animation;
                w.WriteByte(p.Id);
                NetCodec.WriteVec(w, p.Position);
                w.WriteUShort(NetCodec.QuantizeYaw(p.Yaw));
                w.WriteShort(NetCodec.QuantizePitch(p.Pitch));
                w.WriteByte((byte)((p.OnGround ? FlagOnGround : 0) | (p.Crouching ? FlagCrouching : 0) |
                                   (p.Alive ? FlagAlive : 0) | (blending ? FlagBlending : 0)));
                w.WriteByte(p.Life);
                w.WriteByte(p.Weapon);
                NetCodec.WriteAnimation(w, p.Animation, blending);
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
            s.OwnHealth = r.ReadByte();
            s.OwnRespawnTicks = r.ReadUShort();
            s.OwnLife = r.ReadByte();
            s.OwnWeapons = NetCodec.ReadWeapons(r);
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
                if ((flags & ~(FlagOnGround | FlagCrouching | FlagAlive | FlagBlending)) != 0) throw new ProtocolException("Unbekannte Spieler-Flags.");
                p.OnGround = (flags & FlagOnGround) != 0;
                p.Crouching = (flags & FlagCrouching) != 0;
                p.Alive = (flags & FlagAlive) != 0;
                p.Life = r.ReadByte();
                p.Weapon = r.ReadByte();
                if (p.Weapon > CombatSettings.MaxWeapons) throw new ProtocolException($"Ungültiger Waffenplatz {p.Weapon}.");
                p.Animation = NetCodec.ReadAnimation(r, s.ServerTick, (flags & FlagBlending) != 0);
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

        /// <summary>Ein Block ist beschädigt (<see cref="WorldEvent.Level"/> 1 … 255 = Anteil an seinen Lebenspunkten).</summary>
        BlockDamaged = 8,

        /// <summary>Ein Spieler hat Schaden genommen (an alle: Trefferanzeige beim Schützen, Anzeige beim Getroffenen).</summary>
        PlayerDamaged = 9,

        /// <summary>Ein Spieler ist gestorben (an alle, für die Kill-Anzeige). Bei Selbstmord ist der Schütze das Opfer.</summary>
        PlayerKilled = 10,

        /// <summary>Ein anderer Spieler hat geschossen (Mündungsfeuer und Leuchtspur bis zum Einschlag). Nicht an den Schützen.</summary>
        ShotFired = 11,

        /// <summary>Ein Geschoss fliegt los. Der Client rechnet seine Bahn ab <see cref="WorldEvent.Tick"/> zur Anzeige nach.</summary>
        ProjectileSpawned = 12,

        /// <summary>Ein Geschoss explodiert (Blöcke und Schaden kommen als eigene Ereignisse).</summary>
        ProjectileExploded = 13,

        // ---- Team Deathmatch (ab M6) ----

        /// <summary>Ein Spieler ist in einem Team (beim Beitritt für alle anderen, danach bei jedem Wechsel, an alle).</summary>
        TeamChanged = 14,

        /// <summary>Kills und Tode eines Spielers (beim Beitritt für alle, danach nach jedem Tod für Opfer und Schützen).</summary>
        PlayerStats = 15,

        /// <summary>Stand des Matches (<see cref="MatchInfo"/>): Punkte, Phase, Neustart.</summary>
        MatchState = 16,

        /// <summary>Antwort auf einen abgelehnten <see cref="TeamSelectRequest"/> (nur an den Absender).</summary>
        TeamDenied = 17,
    }

    /// <summary>Was ein Schuss getroffen hat (<see cref="WorldEventType.ShotFired"/>).</summary>
    public enum ShotHit : byte
    {
        Nothing = 0,
        Block = 1,
        Player = 2,
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

        // ---- Kampf (ab M5) ----

        /// <summary>Wer geschossen hat (beim Schaden und Kill); <see cref="PlayerId"/> ist dann das Opfer.</summary>
        public byte Attacker;

        /// <summary>Platz der Waffe (siehe weapons.json).</summary>
        public byte Weapon;

        /// <summary>Getroffene Zone (<see cref="Rig.HitZone"/>) oder <see cref="ExplosionZone"/>.</summary>
        public byte Zone;

        public byte Amount;
        public byte Health;
        public byte Level;
        public ShotHit Hit;
        public uint Tick;

        /// <summary>Startpunkt (Schuss, Geschoss) oder Ort der Explosion.</summary>
        public Vec3 Position;

        /// <summary>Endpunkt eines Schusses oder Anfangsgeschwindigkeit eines Geschosses.</summary>
        public Vec3 Vector;

        public int ProjectileId;

        // ---- Team Deathmatch (ab M6) ----

        public Team Team;
        public ushort Kills;
        public ushort Deaths;
        public MatchInfo Match;
        public TeamDenyReason DenyReason;

        /// <summary>Zone bei Schaden durch eine Explosion.</summary>
        public const byte ExplosionZone = 255;
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

        public static void WriteBlockDamaged(NetWriter w, int x, int y, int z, byte level)
        {
            w.WriteByte((byte)WorldEventType.BlockDamaged);
            NetCodec.WriteBlockPos(w, x, y, z);
            w.WriteByte(level);
        }

        public static void WritePlayerDamaged(NetWriter w, byte victim, byte attacker, byte weapon, byte zone, byte amount, byte health)
        {
            w.WriteByte((byte)WorldEventType.PlayerDamaged);
            w.WriteByte(victim);
            w.WriteByte(attacker);
            w.WriteByte(weapon);
            w.WriteByte(zone);
            w.WriteByte(amount);
            w.WriteByte(health);
        }

        public static void WritePlayerKilled(NetWriter w, byte victim, byte killer, byte weapon, byte zone)
        {
            w.WriteByte((byte)WorldEventType.PlayerKilled);
            w.WriteByte(victim);
            w.WriteByte(killer);
            w.WriteByte(weapon);
            w.WriteByte(zone);
        }

        public static void WriteShotFired(NetWriter w, byte shooter, byte weapon, Vec3 from, Vec3 to, ShotHit hit)
        {
            w.WriteByte((byte)WorldEventType.ShotFired);
            w.WriteByte(shooter);
            w.WriteByte(weapon);
            NetCodec.WriteVec(w, from);
            NetCodec.WriteVec(w, to);
            w.WriteByte((byte)hit);
        }

        public static void WriteProjectileSpawned(NetWriter w, Projectile p, uint tick)
        {
            w.WriteByte((byte)WorldEventType.ProjectileSpawned);
            w.WriteVarUInt((uint)p.Id);
            w.WriteByte(p.Weapon);
            w.WriteByte(p.Owner);
            w.WriteUInt(tick);
            NetCodec.WriteVec(w, p.Position);
            NetCodec.WriteVec(w, p.Velocity);
        }

        public static void WriteProjectileExploded(NetWriter w, int id, byte weapon, Vec3 position, uint tick)
        {
            w.WriteByte((byte)WorldEventType.ProjectileExploded);
            w.WriteVarUInt((uint)id);
            w.WriteByte(weapon);
            w.WriteUInt(tick);
            NetCodec.WriteVec(w, position);
        }

        public static void WriteTeamChanged(NetWriter w, byte playerId, Team team)
        {
            w.WriteByte((byte)WorldEventType.TeamChanged);
            w.WriteByte(playerId);
            w.WriteByte((byte)team);
        }

        public static void WritePlayerStats(NetWriter w, byte playerId, int kills, int deaths)
        {
            w.WriteByte((byte)WorldEventType.PlayerStats);
            w.WriteByte(playerId);
            w.WriteUShort((ushort)System.Math.Min(kills, ushort.MaxValue));
            w.WriteUShort((ushort)System.Math.Min(deaths, ushort.MaxValue));
        }

        public static void WriteMatchState(NetWriter w, MatchInfo match)
        {
            w.WriteByte((byte)WorldEventType.MatchState);
            match.Write(w);
        }

        public static void WriteTeamDenied(NetWriter w, TeamDenyReason reason)
        {
            w.WriteByte((byte)WorldEventType.TeamDenied);
            w.WriteByte((byte)reason);
        }

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
                    case WorldEventType.BlockDamaged:
                        NetCodec.ReadBlockPos(r, out e.X, out e.Y, out e.Z);
                        e.Level = r.ReadByte();
                        break;
                    case WorldEventType.PlayerDamaged:
                        e.PlayerId = r.ReadByte();
                        e.Attacker = r.ReadByte();
                        e.Weapon = r.ReadByte();
                        e.Zone = r.ReadByte();
                        e.Amount = r.ReadByte();
                        e.Health = r.ReadByte();
                        break;
                    case WorldEventType.PlayerKilled:
                        e.PlayerId = r.ReadByte();
                        e.Attacker = r.ReadByte();
                        e.Weapon = r.ReadByte();
                        e.Zone = r.ReadByte();
                        break;
                    case WorldEventType.ShotFired:
                        e.PlayerId = r.ReadByte();
                        e.Weapon = r.ReadByte();
                        e.Position = NetCodec.ReadVec(r);
                        e.Vector = NetCodec.ReadVec(r);
                        e.Hit = (ShotHit)r.ReadByte();
                        if (e.Hit > ShotHit.Player) throw new ProtocolException("Unbekannte Trefferart.");
                        break;
                    case WorldEventType.ProjectileSpawned:
                        e.ProjectileId = (int)r.ReadVarUInt();
                        e.Weapon = r.ReadByte();
                        e.PlayerId = r.ReadByte();
                        e.Tick = r.ReadUInt();
                        e.Position = NetCodec.ReadVec(r);
                        e.Vector = NetCodec.ReadVec(r);
                        break;
                    case WorldEventType.ProjectileExploded:
                        e.ProjectileId = (int)r.ReadVarUInt();
                        e.Weapon = r.ReadByte();
                        e.Tick = r.ReadUInt();
                        e.Position = NetCodec.ReadVec(r);
                        break;
                    case WorldEventType.TeamChanged:
                        e.PlayerId = r.ReadByte();
                        e.Team = NetCodec.ReadTeam(r);
                        break;
                    case WorldEventType.PlayerStats:
                        e.PlayerId = r.ReadByte();
                        e.Kills = r.ReadUShort();
                        e.Deaths = r.ReadUShort();
                        break;
                    case WorldEventType.MatchState:
                        e.Match = MatchInfo.Read(r);
                        break;
                    case WorldEventType.TeamDenied:
                        var reason = r.ReadByte();
                        if (reason < (byte)TeamDenyReason.Full || reason > (byte)TeamDenyReason.SameTeam) throw new ProtocolException($"Unbekannter Ablehnungsgrund {reason}.");
                        e.DenyReason = (TeamDenyReason)reason;
                        break;
                    default:
                        throw new ProtocolException($"Unbekanntes Ereignis {(byte)e.Type}.");
                }
                into.Add(e);
            }
        }
    }
}
