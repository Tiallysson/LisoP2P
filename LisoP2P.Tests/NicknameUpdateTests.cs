using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class NicknameUpdateTests
{
    private sealed class RecordingSession(PeerId remoteId) : IPeerSession
    {
        public List<Envelope> Sent { get; } = [];

        public PeerId RemoteId { get; } = remoteId;
        public string RemoteNickname => "";
        public SessionState State => SessionState.Connected;
        public TimeSpan? RoundTripTime => null;

#pragma warning disable CS0067
        public event Action<SessionState>? StateChanged;
        public event Action<Envelope>? MessageReceived;
        public event Action<string>? RemoteNicknameChanged;
#pragma warning restore CS0067

        public Task SendAsync(Envelope envelope, CancellationToken ct)
        {
            Sent.Add(envelope);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(NetworkStream Initiator, NetworkStream Acceptor)> CreateLoopbackPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await acceptTask;
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task NicknameUpdate_UpdatesRemoteNicknameOnTheOtherSide()
    {
        var (initiatorStream, acceptorStream) = await CreateLoopbackPairAsync();

        var identityA = new StubIdentityStore(Guid.NewGuid(), "alice");
        var identityB = new StubIdentityStore(Guid.NewGuid(), "bob");

        var initiator = PeerSession.CreateOutbound(
            identityB.Id, _ => Task.FromResult<Stream>(initiatorStream), identityA, new FakeDiscoveryService());
        var acceptor = PeerSession.CreateInbound(acceptorStream, identityB);

        await Task.WhenAll(initiator.WaitForHandshakeAsync(), acceptor.WaitForHandshakeAsync());

        try
        {
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptor.RemoteNicknameChanged += name => observed.TrySetResult(name);

            await initiator.SendAsync(
                new Envelope
                {
                    Version = ProtocolCodec.CurrentVersion,
                    Type = MessageType.NicknameUpdate,
                    SenderId = identityA.Id.Value,
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = HelloPayloadCodec.Encode(new HelloPayload
                    {
                        Nickname = "  Alice   Nova  ",
                        ProtocolVersion = HelloPayloadCodec.CurrentProtocolVersion,
                    }),
                },
                CancellationToken.None);

            var completed = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(observed.Task, completed);
            Assert.Equal("Alice Nova", observed.Task.Result);
            Assert.Equal("Alice Nova", acceptor.RemoteNickname);
        }
        finally
        {
            await initiator.DisposeAsync();
            await acceptor.DisposeAsync();
        }
    }

    [Fact]
    public async Task NicknameUpdate_WithEmptyNickname_FallsBackToTheIdDerivedName()
    {
        var (initiatorStream, acceptorStream) = await CreateLoopbackPairAsync();

        var identityA = new StubIdentityStore(Guid.NewGuid(), "alice");
        var identityB = new StubIdentityStore(Guid.NewGuid(), "bob");

        var initiator = PeerSession.CreateOutbound(
            identityB.Id, _ => Task.FromResult<Stream>(initiatorStream), identityA, new FakeDiscoveryService());
        var acceptor = PeerSession.CreateInbound(acceptorStream, identityB);

        await Task.WhenAll(initiator.WaitForHandshakeAsync(), acceptor.WaitForHandshakeAsync());

        try
        {
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptor.RemoteNicknameChanged += name => observed.TrySetResult(name);

            await initiator.SendAsync(
                new Envelope
                {
                    Version = ProtocolCodec.CurrentVersion,
                    Type = MessageType.NicknameUpdate,
                    SenderId = identityA.Id.Value,
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = HelloPayloadCodec.Encode(new HelloPayload
                    {
                        Nickname = "\n\t ",
                        ProtocolVersion = HelloPayloadCodec.CurrentProtocolVersion,
                    }),
                },
                CancellationToken.None);

            var completed = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.Same(observed.Task, completed);
            Assert.Equal(NicknameRules.FallbackFor(identityA.Id), observed.Task.Result);
        }
        finally
        {
            await initiator.DisposeAsync();
            await acceptor.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionManager_NicknameChange_NotifiesEveryOpenSession()
    {
        var identity = new StubIdentityStore(Guid.NewGuid(), "alice");
        var manager = new SessionManager(new NetworkOptions(), identity, new FakeDiscoveryService());
        var session = new RecordingSession(new PeerId(Guid.NewGuid()));

        await manager.RegisterSessionAsync(session, isOutbound: true);

        identity.SetNickname("Alice Nova");

        var envelope = Assert.Single(session.Sent);

        Assert.Equal(MessageType.NicknameUpdate, envelope.Type);
        Assert.Equal(identity.Id.Value, envelope.SenderId);
        Assert.True(HelloPayloadCodec.TryDecode(envelope.Payload, out var payload));
        Assert.Equal("Alice Nova", payload!.Nickname);

        await manager.DisposeAsync();
    }
}
