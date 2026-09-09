using LisoP2P.Core;
namespace LisoP2P.Media;

public sealed record AudioSettings
{
    public const int DefaultSampleRate = 48000;
    public const int DefaultChannels = 1;
    public const int DefaultFrameSamples = 960;

    public int SampleRate { get; init; } = DefaultSampleRate;
    public int Channels { get; init; } = DefaultChannels;
    public int FrameSamples { get; init; } = DefaultFrameSamples;
    public int BitrateBps { get; init; } = 24000;
    public AudioCaptureMode Mode { get; init; } = AudioCaptureMode.Microphone;
    public string? InputDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }

    public TimeSpan FrameDuration => TimeSpan.FromMilliseconds(FrameSamples * 1000.0 / SampleRate);
}
