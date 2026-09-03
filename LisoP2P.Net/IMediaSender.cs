using System.Net;
using LisoP2P.Media;

namespace LisoP2P.Net;

public interface IMediaSender : IDisposable
{
    uint FramesSent { get; }
    long BytesSent { get; }

    uint AudioPacketsSent { get; }

    void SendFrame(EncodedFrame frame, IPEndPoint destination);
    void SendAudio(ReadOnlySpan<byte> opusData, IPEndPoint destination);
}
