using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class SessionManagerTieBreakTests
{
    private sealed class FakeSession(PeerId remoteId) : IPeerSession
    {
        public bool Disposed { get; private set; }

        public PeerId RemoteId { get; } = remoteId;
        public string RemoteNickname => "";
        public SessionState State => SessionState.Connected;
        public TimeSpan? RoundTripTime => null;

#pragma warning disable CS0067
        public event Action<SessionState>? StateChanged;
        public event Action<Envelope>? MessageReceived;
        public event Action<string>? RemoteNicknameChanged;
#pragma warning restore CS0067

        public Task SendAsync(Envelope envelope, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RegisterSession_BothSidesConverge_OnTheSameOutcome()
    {
        var smallerId = TestIds.From(0x01);
        var largerId = TestIds.From(0x02);

        // Side with the smaller PeerId keeps the connection it initiated (outbound).
        var managerSmaller = new SessionManager(
            new NetworkOptions(), new StubIdentityStore(smallerId, "a"), new FakeDiscoveryService());
        var smallerOutbound = new FakeSession(largerId);
        var smallerInbound = new FakeSession(largerId);

        await managerSmaller.RegisterSessionAsync(smallerOutbound, isOutbound: true);
        await managerSmaller.RegisterSessionAsync(smallerInbound, isOutbound: false);

        Assert.Same(smallerOutbound, managerSmaller.Sessions[largerId]);
        Assert.True(smallerInbound.Disposed);
        Assert.False(smallerOutbound.Disposed);

        // Side with the larger PeerId keeps the connection it accepted (inbound).
        var managerLarger = new SessionManager(
            new NetworkOptions(), new StubIdentityStore(largerId, "b"), new FakeDiscoveryService());
        var largerOutbound = new FakeSession(smallerId);
        var largerInbound = new FakeSession(smallerId);

        await managerLarger.RegisterSessionAsync(largerOutbound, isOutbound: true);
        await managerLarger.RegisterSessionAsync(largerInbound, isOutbound: false);

        Assert.Same(largerInbound, managerLarger.Sessions[smallerId]);
        Assert.True(largerOutbound.Disposed);
        Assert.False(largerInbound.Disposed);
    }
}
