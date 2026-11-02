using System;

namespace AoS2.Contracts
{
    public sealed class JoinTicketRequest
    {
        /// <summary>Der Spielserver, für den das Ticket gilt, z. B. <c>play.example.org:32887</c>.</summary>
        public string Server { get; set; }
    }

    public sealed class JoinTicketResponse
    {
        /// <summary>Signiertes JWT. Der Client gibt es beim Verbinden an den Spielserver weiter.</summary>
        public string Ticket { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }
}
