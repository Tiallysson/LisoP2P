using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class ChatMessagePayload
{
    [Key(0)] public Guid MessageId { get; init; }
    [Key(1)] public string Text { get; init; } = "";
}
