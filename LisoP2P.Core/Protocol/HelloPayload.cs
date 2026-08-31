using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class HelloPayload
{
    [Key(0)] public string Nickname { get; init; } = "";
    [Key(1)] public byte ProtocolVersion { get; init; }
}
