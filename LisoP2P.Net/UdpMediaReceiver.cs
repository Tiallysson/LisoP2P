using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class UdpMediaReceiver : IMediaReceiver
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Bounds how many senders can allocate reassembly state. A mesh this size never needs more,
    /// and the socket accepts datagrams from anyone, so the dictionary must not grow on demand.
    /// </summary>
    private const int MaxSenders = 8;

    private readonly ConcurrentDictionary<Guid, FrameReassembler> _reassemblers = new();
    private readonly Guid _selfId;

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Timer? _sweepTimer;

    public int PendingFrames => _reassemblers.Values.Sum(r => r.PendingCount);
    public int DroppedFrames => _reassemblers.Values.Sum(r => r.DroppedFrames);

    public event Action<PeerId, DecodableFrame>? FrameReassembled;
    public event Action<PeerId>? FrameDropped;
    public event Action<PeerId, uint, byte[]>? AudioPacketReceived;

    public UdpMediaReceiver(IIdentityStore identity) => _selfId = identity.Id.Value;

    public Task StartAsync(int mediaPort, CancellationToken ct)
    {
        if (_socket is not null)
        {
            return Task.CompletedTask;
        }

        _socket = new UdpClient();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.ReceiveBufferSize = 1 << 20;
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, mediaPort));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _sweepTimer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);

        return Task.CompletedTask;
    }

    public void Reset()
    {
        foreach (var reassembler in _reassemblers.Values)
        {
            reassembler.Reset();
        }
    }

    public void Reset(PeerId sender)
    {
        if (_reassemblers.TryGetValue(sender.Value, out var reassembler))
        {
            reassembler.Reset();
        }
    }

    private void Sweep()
    {
        foreach (var reassembler in _reassemblers.Values)
        {
            reassembler.CollectExpired();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await _socket!.ReceiveAsync(ct).ConfigureAwait(false);
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

            if (!MediaPacketCodec.TryDecode(result.Buffer, out var header, out var payload))
            {
                continue;
            }

            if (header.SenderId == _selfId)
            {
                continue;
            }

            var sender = new PeerId(header.SenderId);

            if (header.StreamId == MediaPacketCodec.AudioStreamId)
            {
                // An Opus voice frame always fits one datagram, so a fragmented audio packet is
                // malformed and never worth reassembling.
                if (header.FragmentCount == 1)
                {
                    AudioPacketReceived?.Invoke(sender, header.FrameId, payload.ToArray());
                }

                continue;
            }

            if (!TryGetReassembler(header.SenderId, out var reassembler))
            {
                continue;
            }

            reassembler.Add(header, payload);
        }
    }

    private bool TryGetReassembler(Guid senderId, out FrameReassembler reassembler)
    {
        if (_reassemblers.TryGetValue(senderId, out reassembler!))
        {
            return true;
        }

        if (_reassemblers.Count >= MaxSenders)
        {
            reassembler = null!;
            return false;
        }

        var sender = new PeerId(senderId);
        var created = new FrameReassembler();
        created.FrameReassembled += frame => FrameReassembled?.Invoke(sender, frame);
        created.FrameDropped += () => FrameDropped?.Invoke(sender);

        reassembler = _reassemblers.GetOrAdd(senderId, created);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sweepTimer is not null)
        {
            await _sweepTimer.DisposeAsync().ConfigureAwait(false);
            _sweepTimer = null;
        }

        _cts?.Cancel();
        _socket?.Close();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _socket?.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _reassemblers.Clear();
    }
}
