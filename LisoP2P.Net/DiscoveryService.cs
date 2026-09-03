using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public sealed class DiscoveryService : IDiscoveryService
{
    private readonly NetworkOptions _options;
    private readonly IIdentityStore _identity;
    private readonly ConcurrentDictionary<Guid, DiscoveredPeer> _peers = new();
    private readonly UdpClient _socket;
    private readonly Timer _announceTimer;
    private readonly Timer _sweepTimer;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public IReadOnlyCollection<DiscoveredPeer> Peers => _peers.Values.ToArray();

    public event Action<DiscoveredPeer>? PeerAppeared;
    public event Action<DiscoveredPeer>? PeerUpdated;
    public event Action<PeerId>? PeerLost;

    public DiscoveryService(NetworkOptions options, IIdentityStore identity)
    {
        _options = options;
        _identity = identity;

        _socket = new UdpClient { EnableBroadcast = true };
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, options.DiscoveryPort));

        _identity.NicknameChanged += OnNicknameChanged;

        _announceTimer = new Timer(_ => SendBroadcastAnnounce(), null, Timeout.Infinite, Timeout.Infinite);
        _sweepTimer = new Timer(_ => SweepExpiredPeers(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);

        _announceTimer.Change(TimeSpan.Zero, _options.AnnounceInterval);
        _sweepTimer.Change(_options.PeerTimeout, _options.PeerTimeout);

        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] buffer, IPEndPoint remote)
    {
        if (!ProtocolCodec.TryDecode(buffer, out var envelope) || envelope is null || envelope.SenderId == _identity.Id.Value)
        {
            return;
        }

        switch (envelope.Type)
        {
            case MessageType.Announce:
                HandleAnnounce(envelope, remote);
                break;
            case MessageType.Goodbye:
                HandleGoodbye(envelope.SenderId);
                break;
        }
    }

    private void OnNicknameChanged(string nickname) => SendBroadcastAnnounce();

    private void HandleAnnounce(Envelope envelope, IPEndPoint remote)
    {
        if (!AnnouncePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        var id = new PeerId(envelope.SenderId);
        var nickname = ResolveNickname(payload.Nickname, id);
        var now = DateTimeOffset.UtcNow;
        var isNew = false;

        var peer = _peers.AddOrUpdate(
            envelope.SenderId,
            _ =>
            {
                isNew = true;
                return new DiscoveredPeer(
                    id, nickname, remote.Address, payload.SessionPort, ResolveMediaPort(payload.MediaPort), now);
            },
            (_, existing) =>
            {
                existing.Nickname = nickname;
                existing.Address = remote.Address;
                existing.SessionPort = payload.SessionPort;
                existing.MediaPort = ResolveMediaPort(payload.MediaPort);
                existing.LastSeen = now;
                return existing;
            });

        if (isNew)
        {
            PeerAppeared?.Invoke(peer);
            SendUnicastAnnounce(remote.Address);
        }
        else
        {
            PeerUpdated?.Invoke(peer);
        }
    }

    private static int ResolveMediaPort(int value) =>
        value is > 0 and <= 65535 ? value : NetworkOptions.DefaultMediaPort;

    private static string ResolveNickname(string? value, PeerId id)
    {
        var sanitized = NicknameRules.Sanitize(value);
        return sanitized.Length > 0 ? sanitized : NicknameRules.FallbackFor(id);
    }

    private void HandleGoodbye(Guid senderId)
    {
        if (_peers.TryRemove(senderId, out _))
        {
            PeerLost?.Invoke(new PeerId(senderId));
        }
    }

    private void SweepExpiredPeers()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.PeerTimeout;
        foreach (var peer in _peers.Values)
        {
            if (peer.LastSeen < cutoff && _peers.TryRemove(peer.Id.Value, out _))
            {
                PeerLost?.Invoke(peer.Id);
            }
        }
    }

    private void SendBroadcastAnnounce()
    {
        var envelope = BuildAnnounceEnvelope();
        foreach (var address in GetTargetAddresses())
        {
            SendEnvelope(envelope, address);
        }
    }

    private void SendUnicastAnnounce(IPAddress target) => SendEnvelope(BuildAnnounceEnvelope(), target);

    private Envelope BuildAnnounceEnvelope()
    {
        var payload = new AnnouncePayload
        {
            Nickname = _identity.Nickname,
            SessionPort = _options.SessionPort,
            MediaPort = _options.MediaPort,
        };
        return new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.Announce,
            SenderId = _identity.Id.Value,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = AnnouncePayloadCodec.Encode(payload),
        };
    }

    private void SendEnvelope(Envelope envelope, IPAddress target)
    {
        var data = ProtocolCodec.Encode(envelope);
        try
        {
            _socket.Send(data, data.Length, new IPEndPoint(target, _options.DiscoveryPort));
        }
        catch (SocketException)
        {
        }
    }

    private static IEnumerable<IPAddress> GetTargetAddresses()
    {
        var addresses = new HashSet<IPAddress>(BroadcastAddressCalculator.GetActiveBroadcastAddresses())
        {
            IPAddress.Broadcast,
        };
        return addresses;
    }

    public async ValueTask DisposeAsync()
    {
        _identity.NicknameChanged -= OnNicknameChanged;

        await _announceTimer.DisposeAsync();
        await _sweepTimer.DisposeAsync();

        var goodbye = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.Goodbye,
            SenderId = _identity.Id.Value,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        foreach (var address in GetTargetAddresses())
        {
            SendEnvelope(goodbye, address);
        }

        _cts?.Cancel();
        _socket.Close();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _socket.Dispose();
        _cts?.Dispose();
    }
}
