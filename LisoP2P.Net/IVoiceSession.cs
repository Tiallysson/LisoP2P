using LisoP2P.Core;
using LisoP2P.Media;

namespace LisoP2P.Net;

public readonly record struct VoiceStats(
    int JitterDepth,
    int ConcealedFrames,
    int LateDiscards,
    int OverflowDiscards,
    uint PacketsSent,
    int PacketsReceived);

public interface IVoiceSession : IAsyncDisposable
{
    bool IsActive { get; }
    bool IsTransmitting { get; }
    bool IsReceiving { get; }
    bool IsPeerSpeaking { get; }
    PeerId? Target { get; }
    float Volume { get; set; }

    event Action? StateChanged;
    event Action<VoiceStats>? StatsUpdated;
    event Action<string>? Log;

    Task StartAsync(CancellationToken ct);
    Task StartVoiceAsync(PeerId target, AudioSettings settings, CancellationToken ct);
    Task StopVoiceAsync();
    void SetTransmitting(bool transmitting);
    void UpdateDevices(string? inputDeviceId, string? outputDeviceId, AudioCaptureMode mode);
}
