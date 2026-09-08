using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class RoomSpeakingPayload
{
    [Key(0)] public Guid RoomId { get; init; }
    [Key(1)] public bool IsSpeaking { get; init; }
}
