using System.Net;
using LisoP2P.Media;

namespace LisoP2P.Net;

public interface IMediaReceiver : IAsyncDisposable
{
    int PendingFrames { get; }
    int DroppedFrames { get; }
    IPAddress? ExpectedSource { get; set; }

    event Action<DecodableFrame>? FrameReassembled;
    event Action? FrameDropped;

    Task StartAsync(int mediaPort, CancellationToken ct);
    void Reset();
}
