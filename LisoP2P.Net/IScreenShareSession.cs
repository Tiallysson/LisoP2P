using LisoP2P.Core;
using LisoP2P.Media;

namespace LisoP2P.Net;

public readonly record struct ScreenShareStats(
    double DecodedFps,
    int DroppedFrames,
    int PendingFrames,
    int KeyframeRequests,
    string DecoderName,
    int BitrateKbps,
    int Fps,
    int ReceiverCount);

public interface IScreenShareSession : IAsyncDisposable
{
    bool IsSharing { get; }
    bool IsWatching { get; }

    /// <summary>Every peer the local screen is being sent to. Empty when not sharing.</summary>
    IReadOnlyList<PeerId> SharingWith { get; }

    PeerId? WatchingFrom { get; }
    int RemoteWidth { get; }
    int RemoteHeight { get; }
    int BitrateKbps { get; }
    int Fps { get; }

    event Action<PreviewFrame>? RemoteFrameReady;
    event Action? StateChanged;
    event Action<ScreenShareStats>? StatsUpdated;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct);

    Task StartSharingAsync(
        IReadOnlyCollection<PeerId> targets,
        int monitorIndex,
        CaptureSettings settings,
        CancellationToken ct);

    /// <summary>
    /// Re-aims an active share after the audience changed: announces to newcomers and re-applies
    /// the bitrate ladder when the receiver count crossed a step.
    /// </summary>
    Task UpdateTargetsAsync(IReadOnlyCollection<PeerId> targets);

    Task StopSharingAsync();
}
