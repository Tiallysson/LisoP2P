using LisoP2P.Core;

namespace LisoP2P.Storage;

public sealed class StoredMessage
{
    public required Guid MessageId { get; init; }
    public required PeerId PeerId { get; init; }
    public required bool IsOutgoing { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public bool Delivered { get; init; }
}
