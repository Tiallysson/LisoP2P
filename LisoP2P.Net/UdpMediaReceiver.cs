using System.Net;
using System.Net.Sockets;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class UdpMediaReceiver : IMediaReceiver
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(50);

    private readonly FrameReassembler _reassembler = new();

    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Timer? _sweepTimer;

    public int PendingFrames => _reassembler.PendingCount;
    public int DroppedFrames => _reassembler.DroppedFrames;
    public IPAddress? ExpectedSource { get; set; }

    public event Action<DecodableFrame>? FrameReassembled;
    public event Action? FrameDropped;
    public event Action<uint, byte[]>? AudioPacketReceived;

    public UdpMediaReceiver()
    {
        _reassembler.FrameReassembled += frame => FrameReassembled?.Invoke(frame);
        _reassembler.FrameDropped += () => FrameDropped?.Invoke();
    }

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
        _sweepTimer = new Timer(_ => _reassembler.CollectExpired(), null, SweepInterval, SweepInterval);

        return Task.CompletedTask;
    }

    public void Reset() => _reassembler.Reset();

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

            var expected = ExpectedSource;

            if (expected is not null && !expected.Equals(result.RemoteEndPoint.Address))
            {
                continue;
            }

            if (!MediaPacketCodec.TryDecode(result.Buffer, out var header, out var payload))
            {
                continue;
            }

            if (header.StreamId == MediaPacketCodec.AudioStreamId)
            {
                if (header.FragmentCount == 1)
                {
                    AudioPacketReceived?.Invoke(header.FrameId, payload.ToArray());
                }

                continue;
            }

            _reassembler.Add(header, payload);
        }
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
    }
}
