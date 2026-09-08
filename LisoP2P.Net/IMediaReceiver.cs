using LisoP2P.Core;
using LisoP2P.Media;

namespace LisoP2P.Net;

public interface IMediaReceiver : IAsyncDisposable
{
    int PendingFrames { get; }
    int DroppedFrames { get; }

    event Action<PeerId, DecodableFrame>? FrameReassembled;
    event Action<PeerId>? FrameDropped;
    event Action<PeerId, uint, byte[]>? AudioPacketReceived;

    Task StartAsync(int mediaPort, CancellationToken ct);

    void Reset();
    void Reset(PeerId sender);
}
