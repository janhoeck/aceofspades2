using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Rig;

namespace AoS2.Shared.Net
{
    /// <summary>
    /// Wie eine Nachricht übertragen wird. Der Transport (LiteNetLib) bildet das auf seine Kanäle ab.
    /// </summary>
    public enum Delivery
    {
        /// <summary>Kann verloren gehen, doppelt oder in falscher Reihenfolge ankommen (Eingaben, mit Wiederholung der letzten).</summary>
        Unreliable,

        /// <summary>Kann verloren gehen; ältere Pakete als das zuletzt empfangene werden verworfen (Snapshots).</summary>
        Sequenced,

        /// <summary>Kommt sicher und in der gesendeten Reihenfolge an (Weltänderungen, Ereignisse).</summary>
        ReliableOrdered,
    }

    /// <summary>Erstes Byte jeder Nachricht.</summary>
    public enum MessageType : byte
    {
        // Client → Server
        Input = 1,
        BlockEdit = 2,

        /// <summary>Teamwunsch (<see cref="TeamSelectRequest"/>).</summary>
        TeamSelect = 3,

        /// <summary>Klassenwunsch (<see cref="ClassSelectRequest"/>), gilt ab dem nächsten Spawn.</summary>
        ClassSelect = 4,

        // Server → Client
        Welcome = 10,
        Snapshot = 11,
        Events = 12,

        /// <summary>Spielregeln des Servers (<see cref="GameDataFiles"/>), kommt vor <see cref="Welcome"/>.</summary>
        GameData = 13,
    }

    /// <summary>Warum eine Verbindung abgelehnt oder getrennt wurde. Steht in <see cref="DisconnectNotice"/>.</summary>
    public enum DisconnectReason : byte
    {
        Unknown = 0,
        ProtocolMismatch = 1,
        InvalidTicket = 2,
        TicketReused = 3,
        ServerFull = 4,
        LoggedInElsewhere = 5,
        ServerShutdown = 6,
        ProtocolError = 7,
    }

    /// <summary>Feste Grenzen des Protokolls. Alles darüber gilt als kaputte Nachricht.</summary>
    public static class NetLimits
    {
        public const int MaxPlayers = 64;
        public const int MaxNameBytes = 64;
        public const int MaxTicketBytes = 4096;
        public const int MaxMapNameBytes = 128;
        public const int MaxMessageBytes = 512;
        public const int ChecksumBytes = 32;

        /// <summary>So viele Eingaben schickt der Client pro Paket mit (die neueste und die davor, falls Pakete verloren gingen).</summary>
        public const int MaxInputsPerPacket = 16;

        /// <summary>Größte fallende Gruppe (siehe <see cref="Voxels.FloatingBlocks.DefaultMaxGroupSize"/>).</summary>
        public const int MaxGroupBlocks = Voxels.FloatingBlocks.DefaultMaxGroupSize;

        /// <summary>Größte Koordinate, die in eine Nachricht passt (uint16).</summary>
        public const int MaxCoordinate = ushort.MaxValue;
    }

    /// <summary>Hilfen für Werte, die mehrere Nachrichten gemeinsam nutzen.</summary>
    public static class NetCodec
    {
        private const byte FlagOnGround = 1;
        private const byte FlagCrouching = 2;

        /// <summary>Voller Spielerzustand ohne Rundung: Der Client vergleicht ihn mit seiner Vorhersage.</summary>
        public static void WriteState(NetWriter w, PlayerState s)
        {
            WriteVec(w, s.Position);
            WriteVec(w, s.Velocity);
            w.WriteByte((byte)((s.OnGround ? FlagOnGround : 0) | (s.Crouching ? FlagCrouching : 0)));
        }

        public static PlayerState ReadState(NetReader r)
        {
            var s = new PlayerState { Position = ReadVec(r), Velocity = ReadVec(r) };
            var flags = r.ReadByte();
            if ((flags & ~(FlagOnGround | FlagCrouching)) != 0) throw new ProtocolException("Unbekannte Zustands-Flags.");
            s.OnGround = (flags & FlagOnGround) != 0;
            s.Crouching = (flags & FlagCrouching) != 0;
            return s;
        }

        public static void WriteVec(NetWriter w, Vec3 v)
        {
            w.WriteFloat(v.X);
            w.WriteFloat(v.Y);
            w.WriteFloat(v.Z);
        }

        public static Vec3 ReadVec(NetReader r) => new Vec3(r.ReadFiniteFloat(), r.ReadFiniteFloat(), r.ReadFiniteFloat());

        /// <summary>Eingabe; nur wenn gefeuert wird, folgt der gesehene Server-Tick (Ganzzahl + 1/256-Bruchteil) für den Lag-Ausgleich.</summary>
        public static void WriteInput(NetWriter w, PlayerInput input)
        {
            w.WriteUShort((ushort)input.Buttons);
            w.WriteFloat(input.Yaw);
            w.WriteFloat(input.Pitch);
            w.WriteByte(input.Weapon);
            if (!input.Has(InputButtons.Fire)) return;
            var view = input.ViewTick < 0 ? 0 : input.ViewTick > uint.MaxValue ? uint.MaxValue : input.ViewTick;
            var whole = (uint)view;
            w.WriteUInt(whole);
            w.WriteByte((byte)((view - whole) * 256));
        }

        public static PlayerInput ReadInput(NetReader r)
        {
            var buttons = r.ReadUShort();
            if ((buttons & ~(ushort)AllButtons) != 0) throw new ProtocolException("Unbekannte Tasten in der Eingabe.");
            var yaw = r.ReadFiniteFloat();
            var pitch = r.ReadFiniteFloat();
            if (yaw < -720f || yaw > 720f || pitch < -90f || pitch > 90f) throw new ProtocolException("Blickrichtung außerhalb des erlaubten Bereichs.");
            var input = new PlayerInput { Buttons = (InputButtons)buttons, Yaw = yaw, Pitch = pitch, Weapon = r.ReadByte() };
            if (input.Weapon > Combat.CombatSettings.MaxWeapons) throw new ProtocolException($"Ungültiger Waffenplatz {input.Weapon}.");
            if (input.Has(InputButtons.Fire)) input.ViewTick = r.ReadUInt() + r.ReadByte() / 256.0;
            return input;
        }

        private const InputButtons AllButtons = InputButtons.Forward | InputButtons.Back | InputButtons.Left | InputButtons.Right |
                                                InputButtons.Jump | InputButtons.Crouch | InputButtons.Sprint |
                                                InputButtons.Fire | InputButtons.Reload;

        /// <summary>Waffen des eigenen Spielers in voller Genauigkeit (für den Abgleich wie die Bewegung).</summary>
        public static void WriteWeapons(NetWriter w, WeaponState s)
        {
            w.WriteByte(s.Slot);
            w.WriteByte(s.Cooldown);
            w.WriteByte(s.Switch);
            w.WriteUShort(s.Reload);
            w.WriteByte((byte)(s.TriggerHeld ? 1 : 0));
            for (var slot = 1; slot <= Combat.CombatSettings.MaxWeapons; slot++)
            {
                var a = s.GetAmmo(slot);
                w.WriteShort(a.Magazine);
                w.WriteShort(a.Reserve);
            }
        }

        public static WeaponState ReadWeapons(NetReader r)
        {
            var s = new WeaponState { Slot = r.ReadByte(), Cooldown = r.ReadByte(), Switch = r.ReadByte(), Reload = r.ReadUShort() };
            var flags = r.ReadByte();
            if (flags > 1 || s.Slot > Combat.CombatSettings.MaxWeapons) throw new ProtocolException("Ungültiger Waffenzustand.");
            s.TriggerHeld = flags == 1;
            for (var slot = 1; slot <= Combat.CombatSettings.MaxWeapons; slot++)
            {
                var a = new WeaponAmmo { Magazine = r.ReadShort(), Reserve = r.ReadShort() };
                if (a.Magazine < 0 || a.Reserve < 0) throw new ProtocolException("Negative Munition.");
                s.SetAmmo(slot, a);
            }
            return s;
        }

        /// <summary>
        /// Animationszustand relativ zum Snapshot-Tick: Ticks als untere 16 Bit (der Server hält die Abstände klein,
        /// siehe <see cref="PlayerAnimator"/>). Die Überblendung kommt nur mit, solange sie läuft.
        /// </summary>
        public static void WriteAnimation(NetWriter w, AnimationState a, bool blending)
        {
            w.WriteByte(a.Animation);
            w.WriteUShort((ushort)a.Start);
            if (!blending) return;
            w.WriteByte(a.Previous);
            w.WriteUShort((ushort)a.PreviousStart);
            w.WriteUShort((ushort)a.BlendStart);
        }

        public static AnimationState ReadAnimation(NetReader r, uint tick, bool blending)
        {
            var a = new AnimationState { Animation = r.ReadByte(), Start = Unwrap(r.ReadUShort(), tick) };
            if (blending)
            {
                a.Previous = r.ReadByte();
                a.PreviousStart = Unwrap(r.ReadUShort(), tick);
                a.BlendStart = Unwrap(r.ReadUShort(), tick);
            }
            else
            {
                a.Previous = a.Animation;
                a.PreviousStart = a.Start;
                a.BlendStart = tick < PlayerAnimator.BlendTicks ? 0 : tick - PlayerAnimator.BlendTicks;
            }
            return a;
        }

        public static Team ReadTeam(NetReader r)
        {
            var team = r.ReadByte();
            if (team > (byte)Team.Green) throw new ProtocolException($"Unbekanntes Team {team}.");
            return (Team)team;
        }

        /// <summary>Volle Tick-Nummer aus den unteren 16 Bit: der letzte Tick bis <paramref name="reference"/> mit diesen Bits.</summary>
        public static uint Unwrap(ushort low, uint reference) => reference - (ushort)(reference - low);

        public static void WriteBlockPos(NetWriter w, int x, int y, int z)
        {
            w.WriteUShort((ushort)x);
            w.WriteUShort((ushort)y);
            w.WriteUShort((ushort)z);
        }

        public static void ReadBlockPos(NetReader r, out int x, out int y, out int z)
        {
            x = r.ReadUShort();
            y = r.ReadUShort();
            z = r.ReadUShort();
        }

        /// <summary>Blickwinkel waagerecht auf 16 Bit (≈ 0,005°). Reicht für die Anzeige anderer Spieler.</summary>
        public static ushort QuantizeYaw(float yaw)
        {
            var normalized = yaw - 360f * DetMath.FloorToInt(yaw / 360f);
            return (ushort)((int)(normalized / 360f * 65536f + 0.5f) & 0xFFFF);
        }

        public static float DequantizeYaw(ushort q) => q * (360f / 65536f);

        public static short QuantizePitch(float pitch) => (short)DetMath.Clamp(pitch / 90f * 32767f, -32767f, 32767f);

        public static float DequantizePitch(short q) => q * (90f / 32767f);
    }
}
