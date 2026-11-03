using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AoS2.JoinTickets;
using AoS2.Shared;
using AoS2.Shared.Net;
using LiteNetLib;
using DisconnectReason = AoS2.Shared.Net.DisconnectReason;
using Microsoft.Extensions.Logging;

namespace AoS2.Server;

/// <summary>
/// Verbindet das <see cref="Match"/> mit dem Netz (LiteNetLib über UDP).
/// <para>
/// Beitritt: Der Client schickt mit der Verbindungsanfrage Protokollversion und Join-Ticket (<see cref="ConnectRequest"/>).
/// Passt die Version nicht, lehnt der Server sofort ab. Das Ticket wird im Hintergrund geprüft; erst danach nimmt er
/// die Verbindung an. Abgelehnte Clients bekommen eine <see cref="DisconnectNotice"/> mit dem Grund.
/// </para>
/// <para>
/// Ohne Verbindung beantwortet er <see cref="ServerQuery"/>-Anfragen (Server-Browser: Ping und Spielerzahl; Backend:
/// Erreichbarkeitsprüfung mit <see cref="ServerProof"/>).
/// </para>
/// Alle LiteNetLib-Ereignisse laufen über <see cref="Update"/> im Thread der Hauptschleife, die Simulation also nie parallel.
/// </summary>
public sealed class GameServer : INetEventListener, IMatchOutput, IDisposable
{
    /// <summary>Höchstens so viele Antworten auf Abfragen pro Sekunde an dieselbe IP (ein Server-Browser fragt einmal pro Aktualisierung).</summary>
    public const int MaxQueriesPerIpPerSecond = 10;

    /// <summary>Höchstens so viele Antworten pro Sekunde insgesamt.</summary>
    public const int MaxQueriesPerSecond = 500;

    private readonly TicketCheck _tickets;
    private readonly ILogger _log;
    private readonly NetManager _net;
    private readonly Dictionary<byte, NetPeer> _peers = new();
    private readonly ConcurrentQueue<(ConnectionRequest Request, JoinTicket? Ticket)> _validated = new();
    private readonly Dictionary<IPAddress, int> _queriesThisSecond = new();
    private int _queriesTotalThisSecond;
    private long _querySecond;
    private int _pendingJoins;
    private int _playerCount;

    public GameServer(GameContent content, MatchSettings settings, TicketCheck tickets, ILogger log, string name = "Ace of Spades 2")
    {
        Match = new Match(content, this, log, settings);
        Name = name;
        _tickets = tickets;
        _log = log;
        _net = new NetManager(this)
        {
            AutoRecycle = true,
            DisconnectTimeout = 10_000,
            UpdateTime = 5,
            ChannelsCount = 1,
            EnableStatistics = true, // Bytes und Pakete auf der Leitung (Log, Lasttest)
            UnconnectedMessagesEnabled = true, // Server-Abfragen (Ping, Erreichbarkeitsprüfung)
        };
    }

    public Match Match { get; }

    /// <summary>Name im Server-Browser.</summary>
    public string Name { get; }

    /// <summary>
    /// Zufälliges Geheimnis dieses Serverprozesses. Geht per Heartbeat (HTTPS) an das Backend; mit ihm beantwortet der Server
    /// die Challenge der Erreichbarkeitsprüfung (<see cref="ServerProof"/>).
    /// </summary>
    public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(ServerListing.SecretBytes);

    /// <summary>Spieler auf dem Server. Darf von anderen Threads gelesen werden (Heartbeat).</summary>
    public int PlayerCount => Volatile.Read(ref _playerCount);

    /// <summary>Tick-Dauer und verschickte Daten (für das Log und den Lasttest).</summary>
    public ServerStats Stats { get; set; } = new();

    /// <summary>Gesendete Bytes und Pakete auf der Leitung (mit LiteNetLib-Kopfdaten und Bestätigungen, ohne UDP/IP-Kopf).</summary>
    public (long Bytes, long Packets) WireSent => (_net.Statistics.BytesSent, _net.Statistics.PacketsSent);

    public (long Bytes, long Packets) WireReceived => (_net.Statistics.BytesReceived, _net.Statistics.PacketsReceived);

    /// <summary>Tatsächlicher Port (bei Port 0 vom Betriebssystem gewählt, für Tests).</summary>
    public int Port => _net.LocalPort;

    /// <summary>Nur für Tests: LiteNetLib verwirft so viel Prozent der Pakete.</summary>
    public int SimulatedPacketLoss
    {
        set
        {
            _net.SimulatePacketLoss = value > 0;
            _net.SimulationPacketLossChance = value;
        }
    }

    public void Start(int port)
    {
        if (!_net.Start(IPAddress.Any, IPAddress.IPv6Any, port))
            throw new InvalidOperationException($"UDP-Port {port} lässt sich nicht öffnen (schon belegt?).");
        _log.LogInformation("Server lauscht auf UDP-Port {Port}", Port);
    }

    /// <summary>Netzwerk-Ereignisse verarbeiten und fertig geprüfte Beitritte annehmen. Vor jedem Tick aufrufen.</summary>
    public void Update()
    {
        _net.PollEvents();
        while (_validated.TryDequeue(out var join))
        {
            _pendingJoins--;
            CompleteJoin(join.Request, join.Ticket);
        }
        Volatile.Write(ref _playerCount, Match.Players.Count);
    }

    /// <summary>Alle Verbindungen mit Begründung trennen und den Port schließen.</summary>
    public void Stop()
    {
        var notice = new DisconnectNotice(DisconnectReason.ServerShutdown, "Der Server wurde beendet.").ToBytes();
        foreach (var peer in _peers.Values.ToList()) peer.Disconnect(notice);
        _net.PollEvents();
        _net.Stop(sendDisconnectMessages: true);
    }

    public void Dispose()
    {
        if (_net.IsRunning) _net.Stop();
    }

    // ---- Beitritt ----

    void INetEventListener.OnConnectionRequest(ConnectionRequest request)
    {
        ConnectRequest connect;
        try
        {
            connect = ConnectRequest.Read(new NetReader(request.Data.GetRemainingBytes()));
        }
        catch (ProtocolException)
        {
            request.Reject(new DisconnectNotice(DisconnectReason.ProtocolError, "Unbekanntes Programm oder Protokoll.").ToBytes());
            return;
        }

        if (!ProtocolInfo.IsCompatible(connect.ProtocolVersion))
        {
            var newer = connect.ProtocolVersion < ProtocolInfo.Version ? "das Spiel" : "der Server";
            Reject(request, DisconnectReason.ProtocolMismatch,
                $"Spiel und Server passen nicht zusammen (Protokoll {connect.ProtocolVersion} gegen {ProtocolInfo.Version}). Bitte {newer} aktualisieren.");
            return;
        }
        if (Match.Players.Count + _pendingJoins >= ServerOptions.MaxPlayersLimit || Match.IsFull)
        {
            Reject(request, DisconnectReason.ServerFull, "Der Server ist voll.");
            return;
        }

        // Die Signaturprüfung kostet etwas Zeit; im Hintergrund, damit die Simulation nicht stockt.
        _pendingJoins++;
        var ticket = connect.Ticket;
        _ = Task.Run(async () =>
        {
            JoinTicket? result = null;
            try
            {
                result = await _tickets.ValidateAsync(ticket);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Ticketprüfung fehlgeschlagen");
            }
            _validated.Enqueue((request, result));
        });
    }

    private void CompleteJoin(ConnectionRequest request, JoinTicket? ticket)
    {
        if (ticket == null)
        {
            Reject(request, DisconnectReason.InvalidTicket, _tickets.InvalidTicketMessage);
            return;
        }
        if (!_tickets.TryUse(ticket))
        {
            Reject(request, DisconnectReason.TicketReused, "Dieses Join-Ticket wurde schon benutzt.");
            return;
        }
        if (Match.IsFull)
        {
            Reject(request, DisconnectReason.ServerFull, "Der Server ist voll.");
            return;
        }

        // Dasselbe Konto ist schon im Spiel (z. B. zweites Fenster): die alte Verbindung trennen.
        foreach (var existing in Match.Players.Where(p => p.AccountId == ticket.AccountId).ToList())
        {
            var oldPeer = _peers[existing.Id];
            Detach(existing.Id);
            oldPeer.Disconnect(new DisconnectNotice(DisconnectReason.LoggedInElsewhere, "Du bist an anderer Stelle beigetreten.").ToBytes());
        }

        var peer = request.Accept();
        if (peer == null) return; // Client hat inzwischen aufgegeben
        var player = Match.AddPlayer(ticket.AccountId, ticket.DisplayName, created =>
        {
            peer.Tag = created;
            _peers[created.Id] = peer;
        });
        _log.LogInformation("{Name} verbunden von {Address}", player.Name, request.RemoteEndPoint);
    }

    private void Reject(ConnectionRequest request, DisconnectReason reason, string message)
    {
        _log.LogInformation("Beitritt von {Address} abgelehnt: {Message}", request.RemoteEndPoint, message);
        request.Reject(new DisconnectNotice(reason, message).ToBytes());
    }

    private void Detach(byte playerId)
    {
        if (!_peers.Remove(playerId, out var peer)) return;
        peer.Tag = null;
        Match.RemovePlayer(playerId);
    }

    // ---- Laufende Verbindungen ----

    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (peer.Tag is ServerPlayer player)
        {
            _log.LogInformation("{Name} getrennt ({Reason})", player.Name, info.Reason);
            Detach(player.Id);
        }
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (peer.Tag is not ServerPlayer player) return;
        if (!Match.HandleMessage(player.Id, reader.RawData, reader.Position, reader.AvailableBytes))
        {
            Detach(player.Id);
            peer.Disconnect(new DisconnectNotice(DisconnectReason.ProtocolError, "Ungültige Nachricht.").ToBytes());
        }
    }

    void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        _log.LogDebug("Netzwerkfehler {Error} ({Address})", socketError, endPoint);

    // ---- Abfragen ohne Verbindung (Server-Browser, Backend) ----

    void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        if (messageType != UnconnectedMessageType.BasicMessage || !AllowQuery(remoteEndPoint.Address)) return;
        ServerQuery query;
        try
        {
            query = ServerQuery.Read(reader.RawData, reader.Position, reader.AvailableBytes);
        }
        catch (ProtocolException)
        {
            return; // fremdes Programm: keine Antwort
        }

        var info = new ServerInfo
        {
            Nonce = query.Nonce,
            ProtocolVersion = ProtocolInfo.Version,
            Proof = query.Challenge != null ? ServerProof.Compute(Secret, query.Challenge) : null,
            Name = Name,
            Map = Match.Content.MapName,
            Players = Match.Players.Count,
            MaxPlayers = Match.Settings.MaxPlayers,
        };
        _net.SendUnconnectedMessage(info.ToBytes(), remoteEndPoint);
    }

    /// <summary>Begrenzt Antworten pro Sekunde (je IP und insgesamt), damit niemand den Server als Paketschleuder nutzt.</summary>
    private bool AllowQuery(IPAddress address)
    {
        var second = Environment.TickCount64 / 1000;
        if (second != _querySecond)
        {
            _querySecond = second;
            _queriesThisSecond.Clear();
            _queriesTotalThisSecond = 0;
        }
        _queriesThisSecond.TryGetValue(address, out var count);
        if (count >= MaxQueriesPerIpPerSecond || _queriesTotalThisSecond >= MaxQueriesPerSecond) return false;
        _queriesThisSecond[address] = count + 1;
        _queriesTotalThisSecond++;
        return true;
    }

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // ---- Senden ----

    void IMatchOutput.Send(byte playerId, NetWriter message, Delivery delivery)
    {
        if (!_peers.TryGetValue(playerId, out var peer)) return;
        peer.Send(message.Buffer, 0, message.Length, ToLiteNetLib(delivery));
        Stats.RecordSend(message.Length, delivery);
    }

    internal static DeliveryMethod ToLiteNetLib(Delivery delivery) => delivery switch
    {
        Delivery.Unreliable => DeliveryMethod.Unreliable,
        Delivery.Sequenced => DeliveryMethod.Sequenced,
        _ => DeliveryMethod.ReliableOrdered,
    };
}
