namespace AoS2.Shared
{
    /// <summary>
    /// Version des Netzwerkprotokolls. Client und Server prüfen sie beim Verbinden.
    /// Bei jeder inkompatiblen Änderung am Protokoll um eins erhöhen.
    /// </summary>
    public static class ProtocolInfo
    {
        public const int Version = 2;

        /// <summary>Standard-UDP-Port der Spielserver.</summary>
        public const int DefaultPort = 32887;

        public static bool IsCompatible(int otherVersion) => otherVersion == Version;
    }
}
