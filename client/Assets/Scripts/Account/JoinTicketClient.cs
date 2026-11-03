using System;
using System.Collections;
using System.Text;
using AoS2.Contracts;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace AoS2.Client.Account
{
    /// <summary>
    /// Holt beim Backend ein Join-Ticket für einen Spielserver (<c>POST /api/join-tickets</c> mit dem Sitzungstoken).
    /// Der Spielserver bekommt nur dieses Ticket, nie das Sitzungstoken.
    /// </summary>
    public static class JoinTicketClient
    {
        private const int TimeoutSeconds = 10;

        /// <param name="server">Adresse des Servers mit Port, genau so steht sie im Ticket.</param>
        public static IEnumerator Request(string server, Action<string> onTicket, Action<string> onError)
        {
            if (!GameSession.IsAvailable)
            {
                onError("Nicht angemeldet. Bitte das Spiel über den Launcher starten oder ein Entwicklungs-Ticket verwenden (Server mit --dev).");
                yield break;
            }

            var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new JoinTicketRequest { Server = server }));
            using (var request = new UnityWebRequest(GameSession.SiteUrl + ApiRoutes.JoinTickets, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(body) { contentType = "application/json" };
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Authorization", "Bearer " + GameSession.Token);
                request.timeout = TimeoutSeconds;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var response = JsonConvert.DeserializeObject<JoinTicketResponse>(request.downloadHandler.text);
                    if (response != null && !string.IsNullOrEmpty(response.Ticket)) onTicket(response.Ticket);
                    else onError("Das Backend hat kein Join-Ticket geliefert.");
                }
                else if (request.responseCode == 401)
                {
                    onError("Anmeldung abgelaufen. Bitte im Launcher neu anmelden.");
                }
                else
                {
                    onError($"Join-Ticket nicht erhalten ({GameSession.SiteUrl}): {request.error}");
                }
            }
        }
    }
}
