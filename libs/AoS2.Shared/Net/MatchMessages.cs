using AoS2.Shared.Maps;

namespace AoS2.Shared.Net
{
    /// <summary>Abschnitt eines Matches (Team Deathmatch).</summary>
    public enum MatchPhase : byte
    {
        /// <summary>Es wird gekämpft, Kills zählen.</summary>
        Playing = 0,

        /// <summary>Ein Team hat gewonnen (oder die Zeit ist um): Ergebnis-Anzeige, niemand kann kämpfen oder bauen, dann Neustart.</summary>
        Ended = 1,
    }

    /// <summary>Warum der Server einen Teamwunsch abgelehnt hat.</summary>
    public enum TeamDenyReason : byte
    {
        /// <summary>Das Team hat schon <see cref="MatchInfo.PlayersPerTeam"/> Spieler.</summary>
        Full = 1,

        /// <summary>Das Team hat schon mehr Spieler als das andere (die Teams dürfen sich höchstens um einen unterscheiden).</summary>
        Unbalanced = 2,

        /// <summary>Man ist schon in diesem Team.</summary>
        SameTeam = 3,
    }

    /// <summary>
    /// Stand des Matches (Ereignis <see cref="WorldEventType.MatchState"/>): Punkte, Phase, Regeln. Kommt beim Beitritt und
    /// bei jeder Änderung (Kill eines Gegners, Matchende, Neustart).
    /// </summary>
    public struct MatchInfo
    {
        public MatchPhase Phase;
        public ushort ScoreBlue;
        public ushort ScoreGreen;

        /// <summary>So viele Punkte braucht ein Team zum Sieg.</summary>
        public ushort ScoreLimit;

        /// <summary>Nach dem Matchende: Gewinner (<see cref="Team.None"/> = unentschieden).</summary>
        public Team Winner;

        /// <summary>
        /// Server-Tick, zu dem die Phase endet: beim Spielen das Zeitlimit (0 = keins), nach dem Matchende der Neustart.
        /// </summary>
        public uint PhaseEndTick;

        /// <summary>Zählt die Matches seit Serverstart (+1 bei jedem Neustart). Ändert sie sich, setzt der Client alles zurück.</summary>
        public byte MatchNumber;

        public byte PlayersPerTeam;
        public bool FriendlyFire;

        public int Score(Team team) => team == Team.Blue ? ScoreBlue : team == Team.Green ? ScoreGreen : 0;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)Phase);
            w.WriteUShort(ScoreBlue);
            w.WriteUShort(ScoreGreen);
            w.WriteUShort(ScoreLimit);
            w.WriteByte((byte)Winner);
            w.WriteUInt(PhaseEndTick);
            w.WriteByte(MatchNumber);
            w.WriteByte(PlayersPerTeam);
            w.WriteByte((byte)(FriendlyFire ? 1 : 0));
        }

        public static MatchInfo Read(NetReader r)
        {
            var m = new MatchInfo();
            var phase = r.ReadByte();
            if (phase > (byte)MatchPhase.Ended) throw new ProtocolException($"Unbekannte Matchphase {phase}.");
            m.Phase = (MatchPhase)phase;
            m.ScoreBlue = r.ReadUShort();
            m.ScoreGreen = r.ReadUShort();
            m.ScoreLimit = r.ReadUShort();
            m.Winner = NetCodec.ReadTeam(r);
            m.PhaseEndTick = r.ReadUInt();
            m.MatchNumber = r.ReadByte();
            m.PlayersPerTeam = r.ReadByte();
            var ff = r.ReadByte();
            if (ff > 1) throw new ProtocolException("Ungültiger Wert für Teambeschuss.");
            m.FriendlyFire = ff == 1;
            return m;
        }
    }

    /// <summary>
    /// Teamwunsch des Clients (zuverlässig): <see cref="Team.Blue"/>, <see cref="Team.Green"/> oder <see cref="Team.None"/> =
    /// automatisch (das kleinere Team). Der Server antwortet mit <see cref="WorldEventType.TeamChanged"/> an alle oder
    /// <see cref="WorldEventType.TeamDenied"/> an den Absender.
    /// </summary>
    public struct TeamSelectRequest
    {
        public Team Team;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.TeamSelect);
            w.WriteByte((byte)Team);
        }

        public static TeamSelectRequest Read(NetReader r)
        {
            var request = new TeamSelectRequest { Team = NetCodec.ReadTeam(r) };
            r.ExpectEnd();
            return request;
        }
    }

    /// <summary>
    /// Klassenwunsch des Clients (zuverlässig), ID aus classes.json (1 …). Gilt ab dem nächsten Spawn: Wer lebt, behält bis
    /// zum nächsten Tod seine Klasse; wer zuschaut oder tot ist, erscheint mit der neuen. Ob die ID gültig ist, prüft der
    /// Server mit seinen Klassen. Die aktive Klasse steht im Snapshot.
    /// </summary>
    public struct ClassSelectRequest
    {
        public byte Class;

        public void Write(NetWriter w)
        {
            w.WriteByte((byte)MessageType.ClassSelect);
            w.WriteByte(Class);
        }

        public static ClassSelectRequest Read(NetReader r)
        {
            var request = new ClassSelectRequest { Class = r.ReadByte() };
            if (request.Class < 1 || request.Class > Combat.PlayerClasses.MaxClasses) throw new ProtocolException($"Ungültige Klasse {request.Class}.");
            r.ExpectEnd();
            return request;
        }
    }
}
