using LisoP2P.Core;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Net;

public interface IRoomChatRouter
{
    /// <summary>
    /// Sends the same message once per session. Returns the MessageId so the caller can persist the
    /// message under the same identity every recipient sees.
    /// </summary>
    Task<Guid> BroadcastAsync(RoomId room, string text, CancellationToken ct);

    event Action<PeerId, ChatMessagePayload>? MessageReceived;

    Task StartAsync(CancellationToken ct);
}
