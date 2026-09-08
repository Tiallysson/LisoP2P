namespace LisoP2P.Core;

public sealed record RoomId(Guid Value)
{
    public static RoomId New() => new(Guid.NewGuid());
}
