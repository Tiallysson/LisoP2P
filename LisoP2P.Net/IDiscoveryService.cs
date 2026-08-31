using LisoP2P.Core;

namespace LisoP2P.Net;

public interface IDiscoveryService : IAsyncDisposable
{
    IReadOnlyCollection<DiscoveredPeer> Peers { get; }
    event Action<DiscoveredPeer>? PeerAppeared;
    event Action<DiscoveredPeer>? PeerUpdated;
    event Action<PeerId>? PeerLost;
    Task StartAsync(CancellationToken ct);
}
