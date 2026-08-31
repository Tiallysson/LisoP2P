using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class ChatAckPayload
{
    [Key(0)] public Guid MessageId { get; init; }
}
