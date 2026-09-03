using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class VoicePayload
{
    [Key(0)] public int SampleRate { get; init; }
    [Key(1)] public int Channels { get; init; }
    [Key(2)] public int FrameSamples { get; init; }
}
