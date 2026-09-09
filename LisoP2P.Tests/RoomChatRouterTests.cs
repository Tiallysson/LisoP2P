using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class RoomChatRouterTests
{
    private static readonly PeerId SelfId = TestIds.From(0x1A);
    private static readonly PeerId PeerB = TestIds.From(0x1B);
    private static readonly PeerId PeerC = TestIds.From(0x1C);

    private sealed class Harness : IAsyncDisposable
    {
        public FakeSessionManager Sessions { get; } = new();
        public FakeDiscoveryWithPeers Discovery { get; } = new();
        public StubIdentityStore Identity { get; } = new(SelfId, "eu");
        public RoomService Rooms { get; }
        public RoomChatRouter Router { get; }

        public Harness()
        {
            Rooms = new RoomService(Identity, Discovery, Sessions);
            Router = new RoomChatRouter(Identity, Sessions, Rooms);
        }

        public async Task<RoomId> StartRoomWithAsync(params PeerId[] peers)
        {
            await Rooms.StartAsync(CancellationToken.None);
            await Router.StartAsync(CancellationToken.None);

            var room = Rooms.CreateRoom("Sala");

            foreach (var peer in peers)
            {
                var session = new FakePeerSession { RemoteId = peer };
                Sessions.Open(session);
                session.Receive(MessageType.RoomJoin, RoomMemberListPayloadCodec.Encode(new RoomMemberListPayload
                {
                    RoomId = room.Value,
                    RoomName = "Sala",
                    Members = [new RoomMemberInfo { PeerId = peer.PublicKeyBytes, Nickname = "n" }],
                }));
            }

            for (var i = 0; i < 100 && Rooms.Members.Count < peers.Length + 1; i++)
            {
                await Task.Delay(10);
            }

            return room;
        }

        public FakePeerSession SessionFor(PeerId peer) => (FakePeerSession)Sessions.Sessions[peer];

        public ValueTask DisposeAsync() => Rooms.DisposeAsync();
    }

    [Fact]
    public async Task Broadcast_SendsTheSameMessageOncePerSession()
    {
        await using var harness = new Harness();
        var room = await harness.StartRoomWithAsync(PeerB, PeerC);

        var messageId = await harness.Router.BroadcastAsync(room, "olá", CancellationToken.None);

        foreach (var peer in new[] { PeerB, PeerC })
        {
            var sent = Assert.Single(harness.SessionFor(peer).Sent, e => e.Type == MessageType.ChatMessage);
            Assert.True(ChatMessagePayloadCodec.TryDecode(sent.Payload, out var payload));
            Assert.Equal(messageId, payload!.MessageId);
            Assert.Equal("olá", payload.Text);
            Assert.Equal(room.Value, payload.RoomId);
        }
    }

    [Fact]
    public async Task IncomingRoomMessage_IsRaisedOnceEvenIfItArrivesTwice()
    {
        await using var harness = new Harness();
        var room = await harness.StartRoomWithAsync(PeerB, PeerC);

        var received = new List<(PeerId Sender, ChatMessagePayload Payload)>();
        harness.Router.MessageReceived += (sender, payload) => received.Add((sender, payload));

        var payload = ChatMessagePayloadCodec.Encode(new ChatMessagePayload
        {
            MessageId = Guid.NewGuid(),
            Text = "oi",
            RoomId = room.Value,
        });

        harness.SessionFor(PeerB).Receive(MessageType.ChatMessage, payload);
        harness.SessionFor(PeerC).Receive(MessageType.ChatMessage, payload);

        var entry = Assert.Single(received);
        Assert.Equal(PeerB, entry.Sender);
        Assert.Equal("oi", entry.Payload.Text);
    }

    [Fact]
    public async Task OneToOneMessage_IsLeftToTheDirectConversation()
    {
        await using var harness = new Harness();
        await harness.StartRoomWithAsync(PeerB);

        var received = 0;
        harness.Router.MessageReceived += (_, _) => received++;

        harness.SessionFor(PeerB).Receive(MessageType.ChatMessage, ChatMessagePayloadCodec.Encode(
            new ChatMessagePayload { MessageId = Guid.NewGuid(), Text = "particular" }));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task MessageForAnotherRoom_IsIgnored()
    {
        await using var harness = new Harness();
        await harness.StartRoomWithAsync(PeerB);

        var received = 0;
        harness.Router.MessageReceived += (_, _) => received++;

        harness.SessionFor(PeerB).Receive(MessageType.ChatMessage, ChatMessagePayloadCodec.Encode(
            new ChatMessagePayload { MessageId = Guid.NewGuid(), Text = "outra", RoomId = Guid.NewGuid() }));

        Assert.Equal(0, received);
    }

    [Fact]
    public async Task OwnBroadcast_IsNotEchoedBackWhenAPeerRelaysIt()
    {
        await using var harness = new Harness();
        var room = await harness.StartRoomWithAsync(PeerB);

        var received = 0;
        harness.Router.MessageReceived += (_, _) => received++;

        var messageId = await harness.Router.BroadcastAsync(room, "eco", CancellationToken.None);

        harness.SessionFor(PeerB).Receive(MessageType.ChatMessage, ChatMessagePayloadCodec.Encode(
            new ChatMessagePayload { MessageId = messageId, Text = "eco", RoomId = room.Value }));

        Assert.Equal(0, received);
    }
}
