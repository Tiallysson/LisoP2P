using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class AnnouncePayload
{
    [Key(0)] public string Nickname { get; init; } = "";
    [Key(1)] public int SessionPort { get; init; }
    [Key(2)] public int MediaPort { get; init; }
}
