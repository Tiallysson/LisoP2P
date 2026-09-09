using System.Net;
using System.Net.Sockets;
using LisoP2P.Core.Protocol;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class PeerSessionHandshakeTests
{
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
    public async Task Handshake_CompletesOnBothSides_AndExchangesNicknames()
    {
        var (initiatorStream, acceptorStream) = await CreateLoopbackPairAsync();

        var identityA = new StubIdentityStore(TestIds.New(), "alice");
        var identityB = new StubIdentityStore(TestIds.New(), "bob");
        var discovery = new FakeDiscoveryService();

        var initiator = PeerSession.CreateOutbound(
            identityB.Id, _ => Task.FromResult<Stream>(initiatorStream), identityA, discovery);
        var acceptor = PeerSession.CreateInbound(acceptorStream, identityB);

        await Task.WhenAll(initiator.WaitForHandshakeAsync(), acceptor.WaitForHandshakeAsync());

        try
        {
            Assert.Equal(SessionState.Connected, initiator.State);
            Assert.Equal(SessionState.Connected, acceptor.State);
            Assert.Equal("bob", initiator.RemoteNickname);
            Assert.Equal("alice", acceptor.RemoteNickname);
            Assert.Equal(identityB.Id, initiator.RemoteId);
            Assert.Equal(identityA.Id, acceptor.RemoteId);
        }
        finally
        {
            await initiator.DisposeAsync();
            await acceptor.DisposeAsync();
        }
    }

    [Fact]
    public async Task Inbound_NonHelloBeforeHandshake_ClosesConnection()
    {
        var (attackerStream, acceptorStream) = await CreateLoopbackPairAsync();
        var identity = new StubIdentityStore(TestIds.New(), "bob");

        var acceptor = PeerSession.CreateInbound(acceptorStream, identity);

        var chatEnvelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.ChatMessage,
            SenderId = TestIds.New().PublicKeyBytes,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = ChatMessagePayloadCodec.Encode(new ChatMessagePayload { MessageId = Guid.NewGuid(), Text = "oi" }),
        };
        await FrameWriter.WriteAsync(attackerStream, chatEnvelope, CancellationToken.None);

        await acceptor.WaitForHandshakeAsync();

        Assert.Equal(SessionState.Closed, acceptor.State);

        await acceptor.DisposeAsync();
        attackerStream.Dispose();
    }
}
