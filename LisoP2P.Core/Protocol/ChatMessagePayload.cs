using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class ChatMessagePayload
{
    [Key(0)] public Guid MessageId { get; init; }
    [Key(1)] public string Text { get; init; } = "";

    /// <summary>Empty for a 1:1 conversation; set when the message belongs to a room.</summary>
    [Key(2)] public Guid RoomId { get; init; }
}
