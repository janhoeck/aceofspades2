using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AoS2.Shared.Net;
using LiteNetLib;

namespace AoS2.Backend.Servers;

public enum ProbeOutcome
{
    /// <summary>Server hat geantwortet und den Nachweis mit dem richtigen Geheimnis geliefert.</summary>
    Reachable,

    /// <summary>Keine Antwort über UDP (Port zu, Firewall, Server aus).</summary>
    NoAnswer,

    /// <summary>Unter der Adresse antwortet ein Spielserver, aber mit falschem Nachweis: ein anderer Server.</summary>
    WrongServer,
}

/// <summary>Prüft per UDP, ob unter einer Adresse wirklich der Server antwortet, der das Geheimnis kennt.</summary>
public interface IServerProbe
{
    Task<(ProbeOutcome Outcome, ServerInfo Info)> ProbeAsync(IPEndPoint endPoint, byte[] secret, CancellationToken cancellationToken);
}

/// <summary>
/// Erreichbarkeitsprüfung über LiteNetLib: schickt dem Server eine <see cref="ServerQuery"/> mit zufälliger Challenge
/// und erwartet in der Antwort <c>HMAC-SHA256(Geheimnis, Challenge)</c> (<see cref="ServerProof"/>). Bis zu drei Versuche,
/// weil UDP Pakete verlieren darf.
/// <para>Ein gemeinsamer UDP-Socket für alle Prüfungen; die Antworten kommen im Thread von LiteNetLib an.</para>
/// </summary>
public sealed class UdpServerProbe : IServerProbe, INetEventListener, IDisposable
{
    public const int Attempts = 3;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(700);

    private readonly ConcurrentDictionary<uint, Pending> _pending = new();
    private readonly NetManager _net;
    private readonly Lock _sendLock = new();

    public UdpServerProbe()
    {
        // UnsyncedEvents: Ereignisse direkt im Empfangs-Thread von LiteNetLib, ohne PollEvents.
        _net = new NetManager(this) { UnconnectedMessagesEnabled = true, UnsyncedEvents = true, AutoRecycle = true };
    }

    public async Task<(ProbeOutcome Outcome, ServerInfo Info)> ProbeAsync(IPEndPoint endPoint, byte[] secret, CancellationToken cancellationToken)
    {
        EnsureStarted();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var challenge = RandomNumberGenerator.GetBytes(ServerQuery.ChallengeBytes);
            uint nonce;
            var pending = new Pending(endPoint);
            do nonce = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); while (!_pending.TryAdd(nonce, pending));
            try
            {
                lock (_sendLock)
                    _net.SendUnconnectedMessage(new ServerQuery { Nonce = nonce, Challenge = challenge }.ToBytes(), endPoint);

                var answered = await Task.WhenAny(pending.Answer.Task, Task.Delay(AttemptTimeout, cancellationToken)) == pending.Answer.Task;
                cancellationToken.ThrowIfCancellationRequested();
                if (!answered) continue;

                var info = pending.Answer.Task.Result;
                return (ServerProof.Matches(secret, challenge, info.Proof) ? ProbeOutcome.Reachable : ProbeOutcome.WrongServer, info);
            }
            finally
            {
                _pending.TryRemove(nonce, out _);
            }
        }
        return (ProbeOutcome.NoAnswer, default);
    }

    private void EnsureStarted()
    {
        lock (_sendLock)
        {
            if (!_net.IsRunning && !_net.Start())
                throw new InvalidOperationException("UDP-Socket für die Erreichbarkeitsprüfung lässt sich nicht öffnen.");
        }
    }

    public void Dispose()
    {
        if (_net.IsRunning) _net.Stop();
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
        // Nur die Antwort von genau der gefragten Adresse zählt.
        if (_pending.TryGetValue(info.Nonce, out var pending) && SameEndPoint(pending.EndPoint, remoteEndPoint))
            pending.Answer.TrySetResult(info);
    }

    private static bool SameEndPoint(IPEndPoint a, IPEndPoint b) =>
        a.Port == b.Port && Normalize(a.Address).Equals(Normalize(b.Address));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

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

    private sealed class Pending(IPEndPoint endPoint)
    {
        public IPEndPoint EndPoint { get; } = endPoint;
        public TaskCompletionSource<ServerInfo> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
