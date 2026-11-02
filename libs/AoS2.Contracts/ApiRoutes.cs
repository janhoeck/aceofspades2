namespace AoS2.Contracts
{
    /// <summary>Pfade der Backend-API, relativ zur Backend-Adresse.</summary>
    public static class ApiRoutes
    {
        /// <summary>
        /// Startet die Discord-Anmeldung im Browser.
        /// Parameter: <see cref="LoginParameters.Port"/>, <see cref="LoginParameters.State"/>, <see cref="LoginParameters.CodeChallenge"/>.
        /// </summary>
        public const string DiscordLogin = "/auth/discord/login";

        /// <summary>Hierhin leitet Discord nach der Anmeldung zurück. Muss in der Discord-App als Redirect eingetragen sein.</summary>
        public const string DiscordCallback = "/auth/discord/callback";

        /// <summary>POST <see cref="SessionRequest"/> → <see cref="SessionResponse"/>: Einmal-Code gegen Sitzungstoken tauschen.</summary>
        public const string Session = "/auth/session";

        /// <summary>POST mit Sitzungstoken: Sitzung beenden.</summary>
        public const string Logout = "/auth/logout";

        /// <summary>GET mit Sitzungstoken → <see cref="AccountInfo"/>.</summary>
        public const string Me = "/me";

        /// <summary>POST <see cref="JoinTicketRequest"/> mit Sitzungstoken → <see cref="JoinTicketResponse"/>.</summary>
        public const string JoinTickets = "/join-tickets";

        /// <summary>Öffentliche Schlüssel, mit denen Spielserver Join-Tickets prüfen (JSON Web Key Set).</summary>
        public const string JoinTicketKeys = "/.well-known/jwks.json";

        public const string Health = "/health";
    }

    /// <summary>
    /// Query-Parameter der Anmeldung. Der Launcher wartet auf <c>http://127.0.0.1:{Port}/callback</c>;
    /// dorthin leitet das Backend am Ende mit <see cref="Code"/> (oder <see cref="Error"/>) und <see cref="State"/> weiter.
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

        /// <summary>Adresse des Backends, ohne „/“ am Ende.</summary>
        public const string BackendUrl = "AOS2_BACKEND_URL";
    }
}
