using LisoP2P.Core;

namespace LisoP2P.Net;

public interface ISessionManager : IAsyncDisposable
{
    IReadOnlyDictionary<PeerId, IPeerSession> Sessions { get; }

    event Action<IPeerSession>? SessionOpened;
    event Action<PeerId>? SessionClosed;

    Task StartListeningAsync(CancellationToken ct);
    Task<IPeerSession> ConnectAsync(DiscoveredPeer peer, CancellationToken ct);
}
