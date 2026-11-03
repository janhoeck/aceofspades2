using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AoS2.Shared.Net;
using LiteNetLib;

namespace AoS2.Net
{
    public enum PingState
    {
        /// <summary>Gefragt, noch keine Antwort.</summary>
        Pending,

        /// <summary>Server hat geantwortet: <see cref="PingResult.RoundTripMs"/> und <see cref="PingResult.Info"/> sind gesetzt.</summary>
        Answered,

        /// <summary>Keine Antwort nach allen Versuchen (Server aus, Port zu, Firewall).</summary>
        NoAnswer,

        /// <summary>Adresse ungültig oder Name nicht auflösbar.</summary>
        Unresolvable,
    }

    /// <summary>Ergebnis einer Abfrage. Wird ersetzt, nicht verändert.</summary>
    public sealed class PingResult
    {
        public PingState State;

        /// <summary>Zeit für Hin- und Rückweg in Millisekunden (nur bei <see cref="PingState.Answered"/>).</summary>
        public int RoundTripMs;

        /// <summary>Aktuelle Angaben des Servers (nur bei <see cref="PingState.Answered"/>).</summary>
        public ServerInfo Info;
    }

    /// <summary>
    /// Misst den Ping zu Spielservern direkt per UDP, ohne Verbindung: schickt eine <see cref="ServerQuery"/> und wartet
    /// auf die <see cref="ServerInfo"/>. Die Zeit dazwischen ist der Ping; die Antwort bringt zusätzlich die aktuelle
    /// Spielerzahl. UDP kann Pakete verlieren, deshalb bis zu <see cref="Attempts"/> Versuche.
    /// <para>
    /// <see cref="Poll"/> regelmäßig im selben Thread aufrufen (einmal pro Bild). Namen werden im Hintergrund aufgelöst,
    /// alles andere passiert in <see cref="Poll"/>.
    /// </para>
    /// </summary>
    public sealed class ServerPinger : INetEventListener, IDisposable
    {
        public const int Attempts = 3;

        /// <summary>Pause zwischen zwei Versuchen in Sekunden; nach dem letzten gilt der Server als stumm.</summary>
        public const double AttemptSeconds = 1.0;

        private readonly NetManager _net;
        private readonly Func<double> _clock;
        private readonly Random _random = new Random();
        private readonly Dictionary<string, PingResult> _results = new Dictionary<string, PingResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Target> _targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, Target> _byNonce = new Dictionary<uint, Target>();
        private readonly ConcurrentQueue<(string Address, IPEndPoint EndPoint)> _resolved = new ConcurrentQueue<(string, IPEndPoint)>();

        /// <param name="clock">Zeit in Sekunden (z. B. <c>Time.realtimeSinceStartupAsDouble</c>).</param>
        public ServerPinger(Func<double> clock)
        {
            _clock = clock;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = true, AutoRecycle = true, UpdateTime = 5 };
        }

        /// <summary>Letzte Ergebnisse je Adresse (wie bei <see cref="Ping"/> angegeben).</summary>
        public IReadOnlyDictionary<string, PingResult> Results => _results;

        public PingResult ResultFor(string address) =>
            address != null && _results.TryGetValue(address, out var result) ? result : null;

        /// <summary>Fragt den Server unter <paramref name="address"/> (<c>host:port</c>) ab. Ein laufender Ping wird neu gestartet.</summary>
        public void Ping(string address)
        {
            if (!_net.IsRunning && !_net.Start()) throw new InvalidOperationException("Netzwerk lässt sich nicht starten.");
            if (_targets.TryGetValue(address, out var old)) Forget(old);
            if (!ServerAddress.TryParse(address, out var parsed))
            {
                _results[address] = new PingResult { State = PingState.Unresolvable };
                return;
            }

            var target = new Target { Address = address };
            _targets[address] = target;
            if (!_results.TryGetValue(address, out var previous) || previous.State != PingState.Answered)
                _results[address] = new PingResult { State = PingState.Pending };

            // DNS kann dauern: im Hintergrund, das Ergebnis holt Poll ab.
            Task.Run(() =>
            {
                IPEndPoint endPoint = null;
                try
                {
                    endPoint = new IPEndPoint(NetUtils.ResolveAddress(parsed.Host), parsed.Port);
                }
                catch (Exception e) when (e is SocketException || e is ArgumentException || e is FormatException)
                {
                }
                _resolved.Enqueue((address, endPoint));
            });
        }

        /// <summary>Antworten verarbeiten, verlorene Anfragen wiederholen.</summary>
        public void Poll()
        {
            if (!_net.IsRunning) return;
            while (_resolved.TryDequeue(out var resolved))
            {
                if (!_targets.TryGetValue(resolved.Address, out var target) || target.EndPoint != null) continue;
                if (resolved.EndPoint == null || resolved.EndPoint.Address.Equals(IPAddress.Any) || resolved.EndPoint.Address.Equals(IPAddress.IPv6Any))
                {
                    Forget(target);
                    _results[target.Address] = new PingResult { State = PingState.Unresolvable };
                    continue;
                }
                target.EndPoint = resolved.EndPoint;
                Send(target);
            }

            _net.PollEvents();

            var now = _clock();
            foreach (var target in new List<Target>(_targets.Values))
            {
                if (target.EndPoint == null || now - target.SentAt < AttemptSeconds) continue;
                if (target.AttemptsMade < Attempts)
                {
                    Send(target);
                    continue;
                }
                Forget(target);
                _results[target.Address] = new PingResult { State = PingState.NoAnswer };
            }
        }

        public void Dispose()
        {
            if (_net.IsRunning) _net.Stop();
        }

        private void Send(Target target)
        {
            _byNonce.Remove(target.Nonce);
            uint nonce;
            do nonce = (uint)_random.Next() ^ ((uint)_random.Next() << 16); while (nonce == 0 || _byNonce.ContainsKey(nonce));
            target.Nonce = nonce;
            target.SentAt = _clock();
            target.AttemptsMade++;
            _byNonce[nonce] = target;
            _net.SendUnconnectedMessage(new ServerQuery { Nonce = nonce }.ToBytes(), target.EndPoint);
        }

        private void Forget(Target target)
        {
            _byNonce.Remove(target.Nonce);
            if (_targets.TryGetValue(target.Address, out var current) && current == target) _targets.Remove(target.Address);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            ServerInfo info;
            try
            {
                info = ServerInfo.Read(reader.RawData, reader.Position, reader.AvailableBytes);
            }
            catch (ProtocolException)
            {
                return;
            }
            if (!_byNonce.TryGetValue(info.Nonce, out var target) || !SameEndPoint(target.EndPoint, remoteEndPoint)) return;

            Forget(target);
            var ms = (int)Math.Round((_clock() - target.SentAt) * 1000);
            _results[target.Address] = new PingResult { State = PingState.Answered, RoundTripMs = Math.Max(0, ms), Info = info };
        }

        /// <summary>Gleiche Gegenstelle, auch wenn eine Seite IPv4 als IPv6 schreibt (<c>::ffff:1.2.3.4</c>).</summary>
        private static bool SameEndPoint(IPEndPoint a, IPEndPoint b)
        {
            if (a == null || b == null || a.Port != b.Port) return false;
            var x = a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address;
            var y = b.Address.IsIPv4MappedToIPv6 ? b.Address.MapToIPv4() : b.Address;
            return x.Equals(y);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request) => request.Reject();

        private sealed class Target
        {
            public string Address;
            public IPEndPoint EndPoint;
            public uint Nonce;
            public double SentAt;
            public int AttemptsMade;
        }
    }
}
