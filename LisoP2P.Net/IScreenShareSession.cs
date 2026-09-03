using LisoP2P.Core;
using LisoP2P.Media;

namespace LisoP2P.Net;

public readonly record struct ScreenShareStats(
    double DecodedFps,
    int DroppedFrames,
    int PendingFrames,
    int KeyframeRequests,
    string DecoderName);

public interface IScreenShareSession : IAsyncDisposable
{
    bool IsSharing { get; }
    bool IsWatching { get; }
    PeerId? SharingWith { get; }
    PeerId? WatchingFrom { get; }
    int RemoteWidth { get; }
    int RemoteHeight { get; }

    event Action<PreviewFrame>? RemoteFrameReady;
    event Action? StateChanged;
    event Action<ScreenShareStats>? StatsUpdated;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct);
    Task StartSharingAsync(PeerId target, int monitorIndex, CaptureSettings settings, CancellationToken ct);
    Task StopSharingAsync();
}
