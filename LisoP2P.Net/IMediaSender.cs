using System.Net;
using LisoP2P.Media;

namespace LisoP2P.Net;

public interface IMediaSender : IDisposable
{
    uint FramesSent { get; }
    long BytesSent { get; }

    void SendFrame(EncodedFrame frame, IPEndPoint destination);
}
