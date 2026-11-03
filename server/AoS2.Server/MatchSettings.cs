namespace AoS2.Server;

/// <summary>
/// Regeln eines Team-Deathmatch, wie der Betreiber sie in der Server-Config festlegt (<see cref="ServerOptions"/>, Datei <c>server.json</c>).
/// Fest sind: ein Punkt pro Kill eines Gegners, Selbstmord und Teamkill zählen nichts.
/// </summary>
public sealed record MatchSettings
{
    /// <summary>Höchstens so viele Spieler pro Team (der Server nimmt doppelt so viele auf).</summary>
    public int PlayersPerTeam { get; init; } = 16;

    /// <summary>Wer zuerst so viele Punkte hat, gewinnt.</summary>
    public int ScoreLimit { get; init; } = 50;

    /// <summary>Zeitlimit eines Matches in Minuten, 0 = keins. Bei Ablauf gewinnt das führende Team, bei Gleichstand niemand.</summary>
    public int TimeLimitMinutes { get; init; }

    /// <summary>So lange steht das Ergebnis nach dem Matchende, dann beginnt das nächste Match.</summary>
    public int MatchEndSeconds { get; init; } = 15;

    /// <summary>Teambeschuss: Können Mitspieler sich gegenseitig verletzen? (Eigene Explosionen schaden immer.)</summary>
    public bool FriendlyFire { get; init; }

    public int MaxPlayers => 2 * PlayersPerTeam;
}
