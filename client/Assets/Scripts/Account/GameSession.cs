using System;
using AoS2.Contracts;
using UnityEngine;

namespace AoS2.Client.Account
{
    /// <summary>
    /// Die Anmeldung, mit der das Spiel gestartet wurde. Der Launcher übergibt Sitzungstoken und Backend-Adresse
    /// als Umgebungsvariablen (<see cref="LaunchEnvironment"/>), nicht auf der Kommandozeile: Die ist für andere
    /// Programme sichtbar (Task-Manager, Prozesslisten, Absturzberichte).
    /// <para>
    /// Im Unity-Editor gibt es keinen Launcher. Dort wird die gespeicherte Anmeldung des Launchers gelesen
    /// (<see cref="LauncherSessionFile"/>), damit man sich nur einmal im Launcher anmelden muss.
    /// </para>
    /// </summary>
    public static class GameSession
    {
        /// <summary>Sitzungstoken für Anfragen an das Backend. <c>null</c>, wenn nicht angemeldet.</summary>
        public static string Token { get; private set; }

        /// <summary>Adresse des Backends ohne „/“ am Ende.</summary>
        public static string BackendUrl { get; private set; }

        public static bool IsAvailable => !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(BackendUrl);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Load()
        {
            Token = Environment.GetEnvironmentVariable(LaunchEnvironment.SessionToken);
            BackendUrl = Environment.GetEnvironmentVariable(LaunchEnvironment.BackendUrl)?.TrimEnd('/');

#if !UNITY_EDITOR
            // Das Token nur in diesem Prozess behalten, nicht an Programme vererben, die das Spiel selbst startet.
            Environment.SetEnvironmentVariable(LaunchEnvironment.SessionToken, null);
#endif

#if UNITY_EDITOR_WIN
            if (string.IsNullOrEmpty(Token))
            {
                var saved = LauncherSessionFile.TryRead();
                if (saved != null)
                {
                    Token = saved.Token;
                    BackendUrl = saved.BackendUrl?.TrimEnd('/');
                    Debug.Log("Editor: nutze die gespeicherte Anmeldung des Launchers.");
                }
            }
#endif
        }
    }
}
