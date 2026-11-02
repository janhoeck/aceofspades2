using System;

namespace AoS2.Contracts
{
    /// <summary>Einmal-Code aus der Anmeldung gegen ein Sitzungstoken tauschen.</summary>
    public sealed class SessionRequest
    {
        public string Code { get; set; }

        /// <summary>Der geheime Zufallswert, zu dem die <see cref="LoginParameters.CodeChallenge"/> gehört.</summary>
        public string CodeVerifier { get; set; }
    }

    public sealed class SessionResponse
    {
        /// <summary>Wird bei jeder Anfrage als <c>Authorization: Bearer …</c> mitgeschickt.</summary>
        public string Token { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public AccountInfo Account { get; set; }
    }

    /// <summary>Das eigene Konto, wie es Launcher und Spiel anzeigen.</summary>
    public sealed class AccountInfo
    {
        public Guid Id { get; set; }

        /// <summary>Anzeigename aus Discord (globaler Name, sonst Benutzername).</summary>
        public string DisplayName { get; set; }

        /// <summary>Profilbild bei Discord, <c>null</c> ohne eigenes Bild.</summary>
        public string AvatarUrl { get; set; }
    }
}
