using System.Buffers;
using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class UdpMediaSender : IMediaSender
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly object _sync = new();
    private readonly Guid _senderId;

    private uint _frameId;
    private uint _audioSequence;
    private bool _disposed;

    public uint FramesSent { get; private set; }
    public uint AudioPacketsSent { get; private set; }
    public long BytesSent { get; private set; }
    public int DroppedFrames { get; private set; }

    /// <summary>
    /// The identity is stamped into every packet here rather than passed per call, so no send path
    /// can forget it and leave the receiver unable to tell mesh senders apart.
    /// </summary>
    public UdpMediaSender(IIdentityStore identity) => _senderId = identity.Id.Value;

    public void SendAudio(ReadOnlySpan<byte> opusData, IReadOnlyCollection<IPEndPoint> destinations)
    {
        if (_disposed || opusData.Length == 0 || opusData.Length > MediaPacketCodec.MaxPayloadSize
            || destinations.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var header = new MediaPacketHeader(
                MediaPacketCodec.CurrentVersion,
                MediaPacketCodec.AudioStreamId,
                _senderId,
                unchecked(_audioSequence++),
                0,
                1,
                0,
                (ushort)opusData.Length);

            var buffer = ArrayPool<byte>.Shared.Rent(MediaPacketCodec.MaxPacketSize);

            try
            {
                var written = MediaPacketCodec.Encode(buffer, header, opusData);

                foreach (var destination in destinations)
                {
                    if (TrySend(buffer.AsSpan(0, written), destination))
                    {
                        BytesSent += written;
                    }
                }

                AudioPacketsSent++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public void SendFrame(EncodedFrame frame, IReadOnlyCollection<IPEndPoint> destinations)
    {
        if (_disposed || frame.Data.Length == 0 || destinations.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var fragmentCount = (frame.Data.Length + MediaPacketCodec.MaxPayloadSize - 1) / MediaPacketCodec.MaxPayloadSize;

            var frameId = unchecked(_frameId++);

            if (fragmentCount > ushort.MaxValue)
            {
                DroppedFrames++;
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(MediaPacketCodec.MaxPacketSize);

            try
            {
                for (var index = 0; index < fragmentCount; index++)
                {
                    var offset = index * MediaPacketCodec.MaxPayloadSize;
                    var length = Math.Min(MediaPacketCodec.MaxPayloadSize, frame.Data.Length - offset);

                    var header = new MediaPacketHeader(
                        MediaPacketCodec.CurrentVersion,
                        MediaPacketCodec.VideoStreamId,
                        _senderId,
                        frameId,
                        (ushort)index,
                        (ushort)fragmentCount,
                        frame.IsKeyframe ? MediaPacketCodec.KeyframeFlag : (byte)0,
                        (ushort)length);

                    var written = MediaPacketCodec.Encode(buffer, header, frame.Data.AsSpan(offset, length));

                    foreach (var destination in destinations)
                    {
                        if (TrySend(buffer.AsSpan(0, written), destination))
                        {
                            BytesSent += written;
                        }
                    }
                }

                FramesSent++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private bool TrySend(ReadOnlySpan<byte> packet, IPEndPoint destination)
    {
        try
        {
            _socket.SendTo(packet, SocketFlags.None, destination);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}
