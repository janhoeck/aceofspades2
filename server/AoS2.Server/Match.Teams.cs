using AoS2.Shared.Combat;
using AoS2.Shared.Maps;
using AoS2.Shared.Movement;
using AoS2.Shared.Net;
using AoS2.Shared.Rig;
using AoS2.Shared.Voxels;
using Microsoft.Extensions.Logging;

namespace AoS2.Server;

/// <summary>
/// Team Deathmatch: Teams, Punkte, Matchende und Neustart.
/// <list type="number">
/// <item>Wer beitritt, ist zuerst Zuschauer (<see cref="Team.None"/>, tot, ohne Respawn) und wählt im Menü ein Team
///       (<see cref="SelectTeam"/>). Ein Team darf dabei höchstens einen Spieler mehr haben als das andere.</item>
/// <item>Jeder Kill eines Gegners gibt dem Team des Schützen einen Punkt. Selbstmord und Teamkill zählen nichts.</item>
/// <item>Hat ein Team <see cref="MatchSettings.ScoreLimit"/> Punkte (oder ist das Zeitlimit um), ist das Match vorbei: Für
///       <see cref="MatchSettings.MatchEndSeconds"/> steht das Ergebnis, niemand kann kämpfen oder bauen.</item>
/// <item>Dann beginnt das nächste Match (<see cref="Restart"/>): Welt wie die Map-Datei, Punkte, Kills und Tode auf 0,
///       alle spawnen neu. Die Teams bleiben.</item>
/// </list>
/// </summary>
public sealed partial class Match
{
    private readonly MatchSettings _settings;
    private readonly int[] _spawnCounters = new int[3];
    private MatchInfo _info;

    /// <summary>Zonen der Map: Spawnzonen (Wände für Gegner, kein Feuern, geschützte Blöcke) und Kampfzone.</summary>
    public MapZones Zones { get; }

    public MatchSettings Settings => _settings;

    /// <summary>Stand des Matches (Punkte, Phase), wie ihn die Clients bekommen.</summary>
    public MatchInfo Info => _info;

    public bool MatchOver => _info.Phase == MatchPhase.Ended;

    /// <summary>Spieler im Team (Zuschauer haben <see cref="Team.None"/>).</summary>
    public int TeamSize(Team team) => _players.Values.Count(p => p.Team == team);

    private MatchInfo NewMatchInfo(byte number) => new()
    {
        Phase = MatchPhase.Playing,
        ScoreLimit = (ushort)Math.Clamp(_settings.ScoreLimit, 1, ushort.MaxValue),
        PhaseEndTick = _settings.TimeLimitMinutes > 0 ? Tick + (uint)(_settings.TimeLimitMinutes * 60 * _content.Movement.TickRate) : 0,
        MatchNumber = number,
        PlayersPerTeam = (byte)Math.Clamp(_settings.PlayersPerTeam, 1, byte.MaxValue),
        FriendlyFire = _settings.FriendlyFire,
    };

    // ---- Teams ----

    /// <summary>
    /// Teamwunsch eines Spielers (<see cref="Team.None"/> = automatisch ins kleinere Team). Erlaubt, wenn das Team nicht voll
    /// ist und danach höchstens einen Spieler mehr hat als das andere. Ein Zuschauer erscheint sofort im Spawn; wer schon
    /// spielt, stirbt beim Wechsel (zählt nicht als Tod) und erscheint nach der Respawn-Zeit im neuen Team.
    /// </summary>
    public TeamDenyReason? SelectTeam(ServerPlayer p, Team wish)
    {
        var team = wish == Team.None ? AutoTeam(p) : wish;
        var denied = CheckTeam(p, team);
        if (denied != null)
        {
            BeginEvent(p.Id);
            WorldEvents.WriteTeamDenied(_events[p.Id], denied.Value);
            return denied;
        }

        var wasPlaying = p.Team != Team.None;
        p.Team = team;
        _scratch.Clear();
        WorldEvents.WriteTeamChanged(_scratch, p.Id, team);
        Broadcast(_scratch);
        // Zuschauer sehen die Restzeit erst richtig, wenn die Uhr wieder läuft (sie steht ohne Spieler).
        BroadcastMatchState();

        if (!wasPlaying)
        {
            Respawn(p);
        }
        else if (p.Alive)
        {
            p.Health = 0;
            p.RespawnAt = Tick + (uint)_content.Combat.RespawnTicks;
        }
        _log.LogInformation("{Name} spielt jetzt für {Team} ({Blue} gegen {Green})", p.Name, TeamLabel(team), TeamSize(Team.Blue), TeamSize(Team.Green));
        return null;
    }

    private TeamDenyReason? CheckTeam(ServerPlayer p, Team team)
    {
        if (team == p.Team) return TeamDenyReason.SameTeam;
        int mine = 0, other = 0;
        foreach (var q in _players.Values)
        {
            if (q == p) continue;
            if (q.Team == team) mine++;
            else if (q.Team == MapMetadata.Opponent(team)) other++;
        }
        if (mine >= _settings.PlayersPerTeam) return TeamDenyReason.Full;
        if (mine > other) return TeamDenyReason.Unbalanced;
        return null;
    }

    /// <summary>Das kleinere Team (ohne den Spieler selbst gezählt); bei Gleichstand das mit weniger Punkten, sonst Blau.</summary>
    private Team AutoTeam(ServerPlayer p)
    {
        int blue = 0, green = 0;
        foreach (var q in _players.Values)
        {
            if (q == p) continue;
            if (q.Team == Team.Blue) blue++;
            else if (q.Team == Team.Green) green++;
        }
        if (blue != green) return blue < green ? Team.Blue : Team.Green;
        return _info.ScoreGreen < _info.ScoreBlue ? Team.Green : Team.Blue;
    }

    // ---- Spawn ----

    /// <summary>
    /// Setzt den Spieler an den nächsten Spawnpunkt seines Teams, mit vollem Leben, voller Munition und vollem Blockvorrat.
    /// Hier wird auch eine neu gewählte Klasse aktiv (Regel: Klassenwechsel beim nächsten Respawn).
    /// </summary>
    private void Respawn(ServerPlayer p)
    {
        var cls = p.NextClass ?? _content.Classes.Default;
        if (cls != p.Class)
        {
            _log.LogInformation("{Name} spielt jetzt {Class}", p.Name, cls.DisplayName);
            p.Class = cls;
        }
        p.State = new PlayerState { Position = Spawns.Pick(World, Metadata, p.Team, _spawnCounters[(int)p.Team]++) };
        p.Remember(p.LastProcessedTick, p.State, p.LastInput); // Blockaktionen ab jetzt vom Spawn aus prüfen
        p.Health = cls.Health;
        p.WeaponsState = WeaponState.Full(cls, p.WeaponsState.Slot);
        p.Blocks = cls.Blocks;
        p.AnimationState = PlayerAnimator.Start(_content.Rig, Tick);
        p.Life++;
        BeginEvent(p.Id);
        WorldEvents.WriteBlockSupply(_events[p.Id], p.Blocks);
    }

    // ---- Klassen ----

    /// <summary>
    /// Klassenwunsch. Die Klasse gilt ab dem nächsten Spawn: Wer zuschaut oder tot ist, erscheint mit ihr; wer lebt, behält
    /// seine bis zum nächsten Tod (sonst könnte man durch einen Wechsel Munition und Leben auffüllen).
    /// </summary>
    public void SelectClass(ServerPlayer p, PlayerClass cls)
    {
        if (p.NextClass == cls) return;
        p.NextClass = cls;
        _log.LogDebug("{Name} wählt {Class} für den nächsten Spawn", p.Name, cls.DisplayName);
    }

    // ---- Punkte, Matchende, Neustart ----

    /// <summary>Kill eines Gegners: ein Punkt für das Team. Erreicht es das Punkteziel, ist das Match vorbei.</summary>
    private void Score(Team team)
    {
        if (MatchOver || team == Team.None) return;
        if (team == Team.Blue) _info.ScoreBlue++;
        else _info.ScoreGreen++;
        if (_info.Score(team) >= _info.ScoreLimit) EndMatch(team);
        else BroadcastMatchState();
    }

    private void EndMatch(Team winner)
    {
        _info.Phase = MatchPhase.Ended;
        _info.Winner = winner;
        _info.PhaseEndTick = Tick + (uint)(Math.Max(1, _settings.MatchEndSeconds) * _content.Movement.TickRate);
        BroadcastMatchState();
        _log.LogInformation("Match vorbei: {Result}, Blau {Blue} : {Green} Grün. Neustart in {Seconds} s",
            winner == Team.None ? "unentschieden" : TeamLabel(winner) + " gewinnt", _info.ScoreBlue, _info.ScoreGreen, _settings.MatchEndSeconds);
    }

    /// <summary>Zu Beginn jedes Ticks: Neustart nach der Ergebnis-Anzeige, Zeitlimit.</summary>
    private void UpdateMatchPhase()
    {
        if (_info.Phase == MatchPhase.Ended)
        {
            if (Tick >= _info.PhaseEndTick) Restart();
            return;
        }
        if (_info.PhaseEndTick == 0) return;
        // Ohne Spieler in Teams läuft die Uhr nicht (ein leerer Server spielt keine Matches durch).
        if (!_players.Values.Any(p => p.Team != Team.None)) _info.PhaseEndTick++;
        else if (Tick >= _info.PhaseEndTick)
            EndMatch(_info.ScoreBlue > _info.ScoreGreen ? Team.Blue : _info.ScoreGreen > _info.ScoreBlue ? Team.Green : Team.None);
    }

    /// <summary>
    /// Nächstes Match: Welt auf den Stand der Map-Datei, fallende Gruppen, Geschosse und Blockschaden weg, Punkte, Kills und
    /// Tode auf 0, alle Spieler mit Team spawnen neu. Die Clients setzen bei der neuen Matchnummer dasselbe zurück und
    /// bekommen jede zurückgesetzte Blockänderung als <see cref="WorldEventType.BlockSet"/>.
    /// </summary>
    public void Restart()
    {
        var fresh = _content.LoadMap().World;
        Falling.Clear();
        _projectiles.Clear();
        BlockDamage.Clear();
        _info = NewMatchInfo((byte)(_info.MatchNumber + 1));
        BroadcastMatchState(); // zuerst: Die Clients vergessen fallende Gruppen, Geschosse und Risse

        var restored = 0;
        foreach (var key in _changes.Keys.ToList())
        {
            Unpack(key, out var x, out var y, out var z);
            var original = fresh.GetBlock(x, y, z);
            if (World.GetBlock(x, y, z) == original) continue;
            World.SetBlock(x, y, z, original);
            _scratch.Clear();
            WorldEvents.WriteBlockSet(_scratch, x, y, z, original);
            Broadcast(_scratch);
            restored++;
        }
        _changes.Clear();

        Array.Clear(_spawnCounters);
        foreach (var p in _players.Values)
        {
            p.Kills = p.Deaths = 0;
            // Offene Blockaktionen aus dem alten Match ablehnen: Der Client nimmt seine Vorhersage dann zurück.
            foreach (var edit in p.Edits)
            {
                BeginEvent(p.Id);
                WorldEvents.WriteEditResult(_events[p.Id], edit.Request.Seq, BlockEditResult.MatchOver, p.Blocks);
            }
            p.Edits.Clear();
            if (p.Team != Team.None) Respawn(p);
        }
        _log.LogInformation("Neues Match ({Restored} Blöcke zurückgesetzt, {Count} Spieler)", restored, _players.Count);
    }

    private void BroadcastMatchState()
    {
        _scratch.Clear();
        WorldEvents.WriteMatchState(_scratch, _info);
        Broadcast(_scratch);
    }

    private void BroadcastStats(ServerPlayer p)
    {
        _scratch.Clear();
        WorldEvents.WritePlayerStats(_scratch, p.Id, p.Kills, p.Deaths);
        Broadcast(_scratch);
    }

    /// <summary>Darf <paramref name="attacker"/> <paramref name="victim"/> Schaden zufügen? (Spawnzone, Teambeschuss, Matchende)</summary>
    private bool CanDamage(ServerPlayer victim, ServerPlayer? attacker)
    {
        if (MatchOver || Zones.InSpawnZone(victim.State.Position)) return false;
        if (attacker == null || attacker == victim || _settings.FriendlyFire) return true;
        return attacker.Team != victim.Team;
    }

    /// <summary>Gehen Schüsse und Raketen von <paramref name="shooter"/> durch <paramref name="other"/> hindurch? (Mitspieler ohne Teambeschuss)</summary>
    private bool PassesThrough(ServerPlayer? shooter, ServerPlayer other) =>
        !_settings.FriendlyFire && shooter != null && shooter != other && shooter.Team != Team.None && shooter.Team == other.Team;

    internal static string TeamLabel(Team team) => team == Team.Blue ? "Blau" : team == Team.Green ? "Grün" : "kein Team";
}
