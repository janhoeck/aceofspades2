namespace AoS2.Contracts
{
    /// <summary>
    /// Pfade der Backend-API, relativ zur Adresse der Website. Alles unter <c>/api</c> reicht die Website
    /// an das Backend durch; Launcher, Spiel und Spielserver brauchen deshalb nur eine Adresse.
    /// </summary>
    public static class ApiRoutes
    {
        /// <summary>POST <see cref="SessionRequest"/> → <see cref="SessionResponse"/>: Einmal-Code gegen Sitzungstoken tauschen.</summary>
        public const string Session = "/api/auth/session";

        /// <summary>POST mit Sitzungstoken: Sitzung beenden.</summary>
        public const string Logout = "/api/auth/logout";

        /// <summary>GET mit Sitzungstoken → <see cref="AccountInfo"/>.</summary>
        public const string Me = "/api/me";

        /// <summary>POST <see cref="JoinTicketRequest"/> mit Sitzungstoken → <see cref="JoinTicketResponse"/>.</summary>
        public const string JoinTickets = "/api/join-tickets";

        /// <summary>Öffentliche Schlüssel, mit denen Spielserver Join-Tickets prüfen (JSON Web Key Set).</summary>
        public const string JoinTicketKeys = "/api/.well-known/jwks.json";

        public const string Health = "/api/health";

        /// <summary>GET → <see cref="ServerListResponse"/>, öffentlich. Parameter <c>protocol</c>: nur Server mit dieser Protokollversion.</summary>
        public const string Servers = "/api/servers";

        /// <summary>POST <see cref="ServerHeartbeatRequest"/> → <see cref="ServerHeartbeatResponse"/> (Spielserver).</summary>
        public const string ServerHeartbeat = "/api/servers/heartbeat";

        /// <summary>POST <see cref="ServerRemoveRequest"/>: Spielserver meldet sich ab.</summary>
        public const string ServerRemove = "/api/servers/remove";
    }

    /// <summary>Seiten der Website (<c>web/</c>), die der Launcher im Browser öffnet.</summary>
    public static class SiteRoutes
    {
        /// <summary>
        /// Startet die Discord-Anmeldung.
        /// Parameter: <see cref="LoginParameters.Port"/>, <see cref="LoginParameters.State"/>, <see cref="LoginParameters.CodeChallenge"/>.
        /// </summary>
        public const string Login = "/login";

        /// <summary>Hierhin leitet Discord nach der Anmeldung zurück. Muss in der Discord-App als Redirect eingetragen sein.</summary>
        public const string DiscordCallback = "/auth/discord/callback";

        /// <summary>Abschlussseite, wenn der Launcher den Einmal-Code bekommen hat.</summary>
        public const string LoginDone = "/login/done";

        /// <summary>Seite für eine abgebrochene oder fehlgeschlagene Anmeldung. Parameter <c>reason</c> = <see cref="LoginErrors"/>.</summary>
        public const string LoginFailed = "/login/error";
    }

    /// <summary>
    /// Query-Parameter der Anmeldung. Der Launcher wartet auf <c>http://127.0.0.1:{Port}/callback</c>;
    /// dorthin leitet die Website am Ende mit <see cref="Code"/> (oder <see cref="Error"/>) und <see cref="State"/> weiter.
    /// </summary>
    public static class LoginParameters
    {
        public const string Port = "port";

        /// <summary>Zufallswert des Launchers. Kommt unverändert zurück, damit der Launcher nur seine eigene Anmeldung annimmt.</summary>
        public const string State = "state";

        /// <summary>Base64url(SHA-256(code_verifier)), wie bei PKCE (RFC 7636).</summary>
        public const string CodeChallenge = "code_challenge";

        public const string Code = "code";
        public const string Error = "error";

        /// <summary>Pfad, unter dem der Launcher die Weiterleitung annimmt.</summary>
        public const string LoopbackPath = "/callback";
    }

    /// <summary>Werte für <see cref="LoginParameters.Error"/>.</summary>
    public static class LoginErrors
    {
        /// <summary>Der Spieler hat bei Discord auf „Abbrechen“ geklickt.</summary>
        public const string AccessDenied = "access_denied";

        /// <summary>Discord hat nicht oder falsch geantwortet.</summary>
        public const string DiscordFailed = "discord_failed";
    }

    /// <summary>Umgebungsvariablen, mit denen der Launcher dem Spiel die Anmeldung übergibt.</summary>
    public static class LaunchEnvironment
    {
        /// <summary>Sitzungstoken. Das Spiel liest es einmal und entfernt die Variable dann aus seinem Prozess.</summary>
        public const string SessionToken = "AOS2_SESSION_TOKEN";

        /// <summary>Adresse der Website (und damit der API unter <c>/api</c>), ohne „/“ am Ende.</summary>
        public const string SiteUrl = "AOS2_SITE_URL";
    }
}
