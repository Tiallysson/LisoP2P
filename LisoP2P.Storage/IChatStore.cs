using LisoP2P.Core;

namespace LisoP2P.Storage;

public interface IChatStore
{
    Task SaveMessageAsync(StoredMessage message);
    Task MarkDeliveredAsync(Guid messageId);
    Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(PeerId peer, int limit = 100);
    Task<IReadOnlyList<StoredMessage>> GetUndeliveredAsync(PeerId peer);
    Task UpsertPeerAsync(PeerId id, string nickname);
}
