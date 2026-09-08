using LisoP2P.Core;

namespace LisoP2P.Net;

public sealed class RoomMember
{
    public required PeerId Id { get; init; }
    public required string Nickname { get; set; }
    public SessionState ConnectionState { get; set; } = SessionState.Connecting;
    public bool IsSpeaking { get; set; }
    public bool IsSharingScreen { get; set; }
    public bool IsSelf { get; init; }
}
