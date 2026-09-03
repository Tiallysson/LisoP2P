namespace LisoP2P.Media;

public interface ICapturePipeline : IAsyncDisposable
{
    IReadOnlyList<CaptureAdapterInfo> AvailableMonitors { get; }
    bool IsRunning { get; }
    string? LogFilePath { get; }
    string? LastRecordingPath { get; }

    Task StartAsync(int monitorIndex, CaptureSettings settings, CancellationToken ct);
    Task StopAsync();
    void RequestKeyframe();

    event Action<EncodedFrame> FrameReady;
    event Action<CaptureStats> StatsUpdated;
    event Action<PreviewFrame> PreviewReady;
    event Action<string> Log;
}
