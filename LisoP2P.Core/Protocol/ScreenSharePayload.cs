using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class ScreenSharePayload
{
    [Key(0)] public int Width { get; init; }
    [Key(1)] public int Height { get; init; }
    [Key(2)] public int Fps { get; init; }
}
