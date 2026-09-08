using LisoP2P.Core;
using LisoP2P.Media;

namespace LisoP2P.Net;

public readonly record struct VoiceStats(
    int JitterDepth,
    int ConcealedFrames,
    int LateDiscards,
    int OverflowDiscards,
    uint PacketsSent,
    int PacketsReceived,
    int ActiveSources);

public interface IVoiceSession : IAsyncDisposable
{
    bool IsActive { get; }
    bool IsTransmitting { get; }
    bool IsReceiving { get; }

    /// <summary>
    /// True while any remote source is producing audio. Only meaningful for the 1:1 window - the
    /// room indicator uses the explicit RoomSpeaking signal instead of inferring from packets.
    /// </summary>
    bool IsPeerSpeaking { get; }

    IReadOnlyList<PeerId> Targets { get; }
    float Volume { get; set; }

    event Action? StateChanged;
    event Action<VoiceStats>? StatsUpdated;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct);
    Task StartVoiceAsync(IReadOnlyCollection<PeerId> targets, AudioSettings settings, CancellationToken ct);
    Task UpdateTargetsAsync(IReadOnlyCollection<PeerId> targets);
    Task StopVoiceAsync();
    void SetTransmitting(bool transmitting);
    void UpdateDevices(string? inputDeviceId, string? outputDeviceId, AudioCaptureMode mode);
}
