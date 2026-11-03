using System;
using System.Net;
using System.Net.Sockets;
using AoS2.Shared;
using AoS2.Shared.Net;
using AoS2.Shared.Net.Client;
using LiteNetLib;
using LiteNetLib.Utils;
using DisconnectReason = AoS2.Shared.Net.DisconnectReason;

namespace AoS2.Net
{
    /// <summary>
    /// Verbindung zu einem Spielserver über LiteNetLib. Reicht empfangene Nachrichten an die <see cref="ClientSession"/>
    /// weiter und schickt deren Nachrichten an den Server; die Spiellogik steckt ganz in der Session.
    /// <para>
    /// <see cref="Poll"/> muss regelmäßig (einmal pro Bild) aufgerufen werden: Erst dann werden empfangene Pakete verarbeitet,
    /// und zwar im aufrufenden Thread. LiteNetLib selbst sendet und empfängt in einem eigenen Hintergrund-Thread.
    /// </para>
    /// </summary>
    public sealed class NetClient : INetEventListener, IMessageSender, IDisposable
    {
        private readonly NetManager _net;
        private NetPeer _peer;

        public NetClient()
        {
            _net = new NetManager(this)
            {
                AutoRecycle = true,
                DisconnectTimeout = 10000,
                UpdateTime = 5,
                ChannelsCount = 1,
            };
        }

        /// <summary>Bekommt alle Nachrichten. Vor <see cref="Connect"/> setzen.</summary>
        public ClientSession Session { get; set; }

        public bool IsConnected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;

        /// <summary>Zeit für Hin- und Rückweg zum Server in Millisekunden.</summary>
        public int RoundTripTime => _peer?.RoundTripTime ?? 0;

        /// <summary>Warum die Verbindung endete, sonst <c>null</c>.</summary>
        public string DisconnectMessage { get; private set; }

        public DisconnectReason? DisconnectCode { get; private set; }

        /// <summary>Nur für Tests: LiteNetLib verwirft so viel Prozent der Pakete.</summary>
        public int SimulatedPacketLoss
        {
            set
            {
                _net.SimulatePacketLoss = value > 0;
                _net.SimulationPacketLossChance = value;
            }
        }

        /// <summary>Verbindung aufbauen. Ergebnis kommt über die Session (Begrüßung) oder <see cref="DisconnectMessage"/>.</summary>
        public void Connect(ServerAddress address, string ticket)
        {
            if (!_net.IsRunning && !_net.Start()) throw new InvalidOperationException("Netzwerk lässt sich nicht starten.");
            var w = new NetWriter(256);
            new ConnectRequest { ProtocolVersion = ProtocolInfo.Version, Ticket = ticket }.Write(w);
            DisconnectMessage = null;
            DisconnectCode = null;
            try
            {
                _peer = _net.Connect(address.Host, address.Port, NetDataWriter.FromBytes(w.ToArray(), false));
            }
            catch (SocketException)
            {
                _peer = null;
            }
            if (_peer == null) Disconnected(Shared.Net.DisconnectReason.Unknown, $"Server \"{address.Host}\" nicht gefunden.");
        }

        public void Poll() => _net.PollEvents();

        public void Disconnect()
        {
            _peer?.Disconnect();
            _peer = null;
        }

        public void Dispose()
        {
            if (_net.IsRunning) _net.Stop();
        }

        void IMessageSender.Send(NetWriter message, Delivery delivery)
        {
            if (!IsConnected) return;
            var method = delivery == Delivery.Unreliable ? DeliveryMethod.Unreliable
                : delivery == Delivery.Sequenced ? DeliveryMethod.Sequenced
                : DeliveryMethod.ReliableOrdered;
            _peer.Send(message.Buffer, 0, message.Length, method);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != _peer) return;
            _peer = null;
            var notice = DisconnectNotice.Parse(info.AdditionalData?.AvailableBytes > 0 ? info.AdditionalData.GetRemainingBytes() : null);
            if (notice.Reason != Shared.Net.DisconnectReason.Unknown && !string.IsNullOrEmpty(notice.Message))
            {
                Disconnected(notice.Reason, notice.Message);
                return;
            }
            string message;
            switch (info.Reason)
            {
                case LiteNetLib.DisconnectReason.ConnectionFailed:
                case LiteNetLib.DisconnectReason.HostUnreachable:
                case LiteNetLib.DisconnectReason.NetworkUnreachable:
                    message = "Der Server ist nicht erreichbar.";
                    break;
                case LiteNetLib.DisconnectReason.Timeout:
                    message = "Keine Antwort mehr vom Server.";
                    break;
                case LiteNetLib.DisconnectReason.ConnectionRejected:
                    message = "Der Server hat die Verbindung abgelehnt.";
                    break;
                case LiteNetLib.DisconnectReason.DisconnectPeerCalled:
                    message = "Verbindung getrennt.";
                    break;
                default:
                    message = "Verbindung zum Server verloren (" + info.Reason + ").";
                    break;
            }
            Disconnected(Shared.Net.DisconnectReason.Unknown, message);
        }

        private void Disconnected(DisconnectReason reason, string message)
        {
            DisconnectCode = reason;
            DisconnectMessage = message;
            Session?.OnDisconnected(message);
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (peer != _peer || Session == null) return;
            Session.HandleMessage(reader.RawData, reader.Position, reader.AvailableBytes);
            // Die Session hat aufgegeben (z. B. andere Map-Version): Verbindung sauber schließen.
            if (Session.Phase == ClientPhase.Disconnected) Disconnect();
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
