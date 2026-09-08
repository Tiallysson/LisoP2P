using System.Net;
using LisoP2P.Media;

namespace LisoP2P.Net;

public interface IMediaSender : IDisposable
{
    uint FramesSent { get; }
    long BytesSent { get; }

    uint AudioPacketsSent { get; }

    /// <summary>
    /// Sends one encoded frame to every destination. The frame is fragmented once and each fragment
    /// goes out to all destinations, so a mesh costs upload bandwidth but never a second encode.
    /// </summary>
    void SendFrame(EncodedFrame frame, IReadOnlyCollection<IPEndPoint> destinations);

    void SendAudio(ReadOnlySpan<byte> opusData, IReadOnlyCollection<IPEndPoint> destinations);
}
