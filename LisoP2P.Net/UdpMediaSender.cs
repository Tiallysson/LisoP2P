using System.Buffers;
using System.Net;
using System.Net.Sockets;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;

namespace LisoP2P.Net;

public sealed class UdpMediaSender : IMediaSender
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly object _sync = new();

    private uint _frameId;
    private bool _disposed;

    public uint FramesSent { get; private set; }
    public long BytesSent { get; private set; }
    public int DroppedFrames { get; private set; }

    public void SendFrame(EncodedFrame frame, IPEndPoint destination)
    {
        if (_disposed || frame.Data.Length == 0)
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
                        MediaPacketCodec.DefaultStreamId,
                        frameId,
                        (ushort)index,
                        (ushort)fragmentCount,
                        frame.IsKeyframe ? MediaPacketCodec.KeyframeFlag : (byte)0,
                        (ushort)length);

                    var written = MediaPacketCodec.Encode(buffer, header, frame.Data.AsSpan(offset, length));

                    try
                    {
                        _socket.SendTo(buffer.AsSpan(0, written), SocketFlags.None, destination);
                        BytesSent += written;
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
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

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}
