using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class RoomMemberInfo
{
    [Key(0)] public Guid PeerId { get; init; }
    [Key(1)] public string Nickname { get; init; } = "";
}

/// <summary>
/// Carries the sender's view of a room. Used by RoomInvite, RoomJoin and RoomMemberList alike:
/// all three are "here is who I think is in this room", differing only in intent.
/// </summary>
[MessagePackObject]
public sealed class RoomMemberListPayload
{
    [Key(0)] public Guid RoomId { get; init; }
    [Key(1)] public List<RoomMemberInfo> Members { get; init; } = [];
    [Key(2)] public string RoomName { get; init; } = "";
}
