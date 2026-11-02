using System;

namespace AoS2.Contracts
{
    /// <summary>
    /// Die Anmeldung, die der Launcher zwischen zwei Starts speichert:
    /// <c>%LocalAppData%\AceOfSpades2\session.dat</c>, verschlüsselt mit Windows DPAPI (nur dieser Windows-Benutzer kann sie lesen).
    /// Der Unity-Editor liest sie ebenfalls, damit man beim Entwickeln nicht über den Launcher starten muss.
    /// </summary>
    public sealed class SavedSession
    {
        /// <summary>Datenordner unter <c>%LocalAppData%</c>. Zum Testen über die Umgebungsvariable <c>AOS2_DATA_DIR</c> umlenkbar.</summary>
        public const string DataFolderName = "AceOfSpades2";

        public const string DataDirVariable = "AOS2_DATA_DIR";

        public const string FileName = "session.dat";

        /// <summary>
        /// Zusätzlicher Schlüsselteil für DPAPI. Andere Programme, die einfach „entschlüssele das“ versuchen, scheitern daran.
        /// </summary>
        public const string Entropy = "AoS2.SavedSession.v1";

        public string Token { get; set; }

        /// <summary>Website (mit API unter <c>/api</c>), deren Backend das Token ausgestellt hat. Das Token gilt nur dort.</summary>
        public string SiteUrl { get; set; }

        public Guid AccountId { get; set; }

        /// <summary>Für die Anzeige, wenn das Backend gerade nicht erreichbar ist.</summary>
        public string DisplayName { get; set; }
    }
}
