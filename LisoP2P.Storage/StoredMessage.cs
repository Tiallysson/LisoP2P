using LisoP2P.Core;

namespace LisoP2P.Storage;

public sealed class StoredMessage
{
    public required Guid MessageId { get; init; }

    /// <summary>
    /// The other party in a 1:1 conversation. For a room message it is the sender instead, so a
    /// bubble can show who wrote it.
    /// </summary>
    public required PeerId PeerId { get; init; }

    public required bool IsOutgoing { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public bool Delivered { get; init; }

    /// <summary>Null for the 1:1 history that predates rooms.</summary>
    public RoomId? RoomId { get; init; }
}
