using System.Collections;
using AoS2.Contracts;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AoS2.Client.Account
{
    public enum AccountState
    {
        Checking,
        SignedIn,

        /// <summary>Ohne Launcher gestartet (und im Editor keine gespeicherte Anmeldung).</summary>
        NotSignedIn,

        /// <summary>Das Backend kennt das Token nicht mehr (abgemeldet oder abgelaufen).</summary>
        Expired,

        /// <summary>Backend nicht erreichbar.</summary>
        Unreachable,
    }

    /// <summary>
    /// Fragt beim Start mit dem Sitzungstoken beim Backend nach, wer angemeldet ist (<c>GET /api/me</c>).
    /// Erzeugt sich selbst, braucht also nichts in der Szene.
    /// </summary>
    public sealed class PlayerAccount : MonoBehaviour
    {
        private const int TimeoutSeconds = 10;

        public static AccountState State { get; private set; } = AccountState.Checking;

        /// <summary>Das eigene Konto, sobald <see cref="State"/> <see cref="AccountState.SignedIn"/> ist.</summary>
        public static AccountInfo Account { get; private set; }

        public static bool IsSignedIn => State == AccountState.SignedIn;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            State = AccountState.Checking;
            Account = null;
            var go = new GameObject(nameof(PlayerAccount));
            go.AddComponent<PlayerAccount>();
            DontDestroyOnLoad(go);
        }

        private IEnumerator Start()
        {
            if (!GameSession.IsAvailable)
            {
                State = AccountState.NotSignedIn;
                Debug.Log("Nicht angemeldet: Das Spiel wurde ohne Launcher gestartet.");
                yield break;
            }

            using (var request = UnityWebRequest.Get(GameSession.SiteUrl + ApiRoutes.Me))
            {
                request.SetRequestHeader("Authorization", "Bearer " + GameSession.Token);
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Account = JsonConvert.DeserializeObject<AccountInfo>(request.downloadHandler.text);
                    State = AccountState.SignedIn;
                    Debug.Log($"Angemeldet als {Account.DisplayName}");
                }
                else if (request.responseCode == 401)
                {
                    State = AccountState.Expired;
                    Debug.LogWarning("Anmeldung abgelaufen. Bitte im Launcher neu anmelden.");
                }
                else
                {
                    State = AccountState.Unreachable;
                    Debug.LogWarning($"Backend nicht erreichbar ({GameSession.SiteUrl}): {request.error}");
                }
            }
        }
    }
}
