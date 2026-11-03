using System;
using System.Collections.Generic;
using System.Linq;
using AoS2.Contracts;
using AoS2.Shared;

namespace AoS2.Net
{
    public enum ServerSort
    {
        /// <summary>Volle Server zuerst (dort ist am meisten los), bei Gleichstand der kleinere Ping.</summary>
        Players,
        Ping,
        Name,
    }

    /// <summary>Was der Spieler im Server-Browser ausblenden möchte.</summary>
    public sealed class ServerFilter
    {
        public bool HideFull;
        public bool HideEmpty;

        /// <summary>Nur Server mit dieser Map, <c>null</c> = alle.</summary>
        public string Map;

        public ServerSort Sort = ServerSort.Players;
    }

    /// <summary>Ein Eintrag, wie der Server-Browser ihn zeigt: Liste vom Backend plus eigener Ping.</summary>
    public sealed class ServerRow
    {
        public ServerListEntry Entry;
        public PingResult Ping;

        /// <summary>Aktuelle Spielerzahl: aus der Antwort des Servers, sonst vom letzten Heartbeat.</summary>
        public int Players => Ping?.State == PingState.Answered ? Ping.Info.Players : Entry.Players;

        public int MaxPlayers => Ping?.State == PingState.Answered ? Ping.Info.MaxPlayers : Entry.MaxPlayers;

        public bool IsFull => Players >= MaxPlayers;

        /// <summary>Ping in Millisekunden, <c>null</c> ohne Antwort.</summary>
        public int? PingMs => Ping?.State == PingState.Answered ? Ping.RoundTripMs : (int?)null;
    }

    /// <summary>
    /// Die Serverliste des Server-Browsers ohne Darstellung: blendet Server mit anderer Protokollversion aus, wendet die Filter
    /// an und sortiert. Offizielle Server stehen immer oben.
    /// </summary>
    public sealed class ServerList
    {
        private readonly List<ServerListEntry> _entries = new List<ServerListEntry>();

        public IReadOnlyList<ServerListEntry> Entries => _entries;

        /// <summary>Neue Liste vom Backend. Einträge mit anderer Protokollversion fallen sofort heraus.</summary>
        public void Set(IEnumerable<ServerListEntry> entries)
        {
            _entries.Clear();
            if (entries == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
                if (e != null && !string.IsNullOrEmpty(e.Address) && ProtocolInfo.IsCompatible(e.ProtocolVersion) && seen.Add(e.Address))
                    _entries.Add(e);
        }

        /// <summary>Alle Maps der passenden Server, für den Map-Filter.</summary>
        public IReadOnlyList<string> Maps =>
            _entries.Select(e => e.Map).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Was der Server-Browser anzeigt.</summary>
        /// <param name="ping">Ping-Ergebnis zu einer Adresse oder <c>null</c> (z. B. <see cref="ServerPinger.ResultFor"/>).</param>
        public IReadOnlyList<ServerRow> Visible(ServerFilter filter, Func<string, PingResult> ping)
        {
            var rows = _entries.Select(e => new ServerRow { Entry = e, Ping = ping?.Invoke(e.Address) })
                // Antwortet der Server mit einer anderen Version als im Heartbeat (gerade aktualisiert), ebenfalls ausblenden.
                .Where(r => r.Ping?.State != PingState.Answered || r.Ping.Info.IsCompatible)
                .Where(r => !filter.HideFull || !r.IsFull)
                .Where(r => !filter.HideEmpty || r.Players > 0)
                .Where(r => filter.Map == null || string.Equals(r.Entry.Map, filter.Map, StringComparison.OrdinalIgnoreCase));

            var official = rows.OrderByDescending(r => r.Entry.Official);
            IOrderedEnumerable<ServerRow> sorted;
            switch (filter.Sort)
            {
                case ServerSort.Ping:
                    sorted = official.ThenBy(r => r.PingMs ?? int.MaxValue).ThenByDescending(r => r.Players);
                    break;
                case ServerSort.Name:
                    sorted = official.ThenBy(r => r.Entry.Name, StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    sorted = official.ThenByDescending(r => r.Players).ThenBy(r => r.PingMs ?? int.MaxValue);
                    break;
            }
            return sorted.ThenBy(r => r.Entry.Address, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
