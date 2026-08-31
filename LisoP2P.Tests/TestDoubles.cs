using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.Tests;

internal sealed class StubIdentityStore(Guid id, string nickname) : IIdentityStore
{
    public PeerId Id { get; } = new(id);
    public string Nickname { get; } = nickname;
}

internal sealed class FakeDiscoveryService : IDiscoveryService
{
    public IReadOnlyCollection<DiscoveredPeer> Peers => [];

#pragma warning disable CS0067 // required by IDiscoveryService, unused by tests that only need PeerLost wiring
    public event Action<DiscoveredPeer>? PeerAppeared;
    public event Action<DiscoveredPeer>? PeerUpdated;
#pragma warning restore CS0067
    public event Action<PeerId>? PeerLost;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void RaisePeerLost(PeerId id) => PeerLost?.Invoke(id);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
