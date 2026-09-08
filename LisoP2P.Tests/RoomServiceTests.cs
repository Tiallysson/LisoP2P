using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class RoomServiceTests
{
    private static readonly Guid SelfId = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly PeerId PeerB = new(Guid.Parse("0b000000-0000-0000-0000-00000000000b"));
    private static readonly PeerId PeerC = new(Guid.Parse("0c000000-0000-0000-0000-00000000000c"));
    private static readonly PeerId PeerD = new(Guid.Parse("0d000000-0000-0000-0000-00000000000d"));

    private sealed class Harness : IAsyncDisposable
    {
        public FakeSessionManager Sessions { get; } = new();
        public FakeDiscoveryWithPeers Discovery { get; } = new();
        public StubIdentityStore Identity { get; } = new(SelfId, "eu");
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public RoomService Rooms { get; }

        public Harness() => Rooms = new RoomService(Identity, Discovery, Sessions, () => Now);

        public FakePeerSession Open(PeerId id)
        {
            var session = new FakePeerSession { RemoteId = id };
            Sessions.Open(session);
            return session;
        }

        public ValueTask DisposeAsync() => Rooms.DisposeAsync();
    }

    private static byte[] MemberList(RoomId room, params PeerId[] members) =>
        RoomMemberListPayloadCodec.Encode(new RoomMemberListPayload
        {
            RoomId = room.Value,
            RoomName = "Sala",
            Members = [.. members.Select(m => new RoomMemberInfo { PeerId = m.Value, Nickname = "n" })],
        });

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task CreateRoom_StartsWithOnlyTheLocalPeer()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        harness.Rooms.CreateRoom("Equipe");

        var member = Assert.Single(harness.Rooms.Members);
        Assert.Equal(SelfId, member.Id.Value);
        Assert.True(member.IsSelf);
        Assert.Equal("Equipe", harness.Rooms.RoomName);
        Assert.Empty(harness.Rooms.RemoteMemberIds);
    }

    [Fact]
    public async Task MemberList_FromAThirdPeer_ConvergesWithoutDuplicatingEntries()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        // A already knows {A, B}: B accepted an invitation and answered with its own view.
        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));

        // C now gossips {A, B, C, D}.
        var sessionC = harness.Open(PeerC);
        sessionC.Receive(MessageType.RoomMemberList, MemberList(room, new PeerId(SelfId), PeerB, PeerC, PeerD));

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 4));

        var ids = harness.Rooms.Members.Select(m => m.Id.Value).ToList();

        Assert.Equal(4, ids.Distinct().Count());
        Assert.Contains(SelfId, ids);
        Assert.Contains(PeerB.Value, ids);
        Assert.Contains(PeerC.Value, ids);
        Assert.Contains(PeerD.Value, ids);
        Assert.Single(harness.Rooms.Members, m => m.IsSelf);
    }

    [Fact]
    public async Task MemberList_ReceivedTwice_DoesNotDuplicateOrReAnnounce()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));

        var sessionC = harness.Open(PeerC);
        sessionC.Receive(MessageType.RoomMemberList, MemberList(room, PeerB, PeerC));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 3));

        var announcementsToB = sessionB.Sent.Count(e => e.Type == MessageType.RoomMemberList);

        // Nothing new in the second copy, so it must not start another round of gossip.
        sessionC.Receive(MessageType.RoomMemberList, MemberList(room, PeerB, PeerC));
        await Task.Delay(50);

        Assert.Equal(3, harness.Rooms.Members.Count);
        Assert.Equal(announcementsToB, sessionB.Sent.Count(e => e.Type == MessageType.RoomMemberList));
    }

    [Fact]
    public async Task MemberList_ForAnotherRoom_IsIgnored()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomMemberList, MemberList(RoomId.New(), PeerB, PeerC));

        await Task.Delay(50);

        Assert.Single(harness.Rooms.Members);
    }

    [Fact]
    public async Task Invite_AcceptedByTheRemotePeer_AnswersWithRoomJoin()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = RoomId.New();
        var sessionB = harness.Open(PeerB);

        sessionB.Receive(MessageType.RoomInvite, MemberList(room, PeerB, PeerC));

        Assert.True(await WaitForAsync(() => sessionB.Sent.Any(e => e.Type == MessageType.RoomJoin)));
        Assert.Equal(room, harness.Rooms.CurrentRoom);
        Assert.Equal(3, harness.Rooms.Members.Count);
    }

    [Fact]
    public async Task Speaking_ClearsItselfWhenTheStartSignalIsNeverRenewed()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));

        sessionB.Receive(MessageType.RoomSpeaking, RoomSpeakingPayloadCodec.Encode(
            new RoomSpeakingPayload { RoomId = room.Value, IsSpeaking = true }));

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Single(m => m.Id == PeerB).IsSpeaking));

        // Well inside the window nothing changes.
        harness.Now += TimeSpan.FromSeconds(2);
        harness.Rooms.Tick();
        Assert.True(harness.Rooms.Members.Single(m => m.Id == PeerB).IsSpeaking);

        // Past it the indicator releases on its own, without a "stopped" ever arriving.
        harness.Now += TimeSpan.FromSeconds(2);
        harness.Rooms.Tick();
        Assert.False(harness.Rooms.Members.Single(m => m.Id == PeerB).IsSpeaking);
    }

    [Fact]
    public async Task Speaking_StopSignal_ClearsImmediately()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));

        sessionB.Receive(MessageType.RoomSpeaking, RoomSpeakingPayloadCodec.Encode(
            new RoomSpeakingPayload { RoomId = room.Value, IsSpeaking = true }));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Single(m => m.Id == PeerB).IsSpeaking));

        sessionB.Receive(MessageType.RoomSpeaking, RoomSpeakingPayloadCodec.Encode(
            new RoomSpeakingPayload { RoomId = room.Value, IsSpeaking = false }));

        Assert.True(await WaitForAsync(() => !harness.Rooms.Members.Single(m => m.Id == PeerB).IsSpeaking));
    }

    [Fact]
    public async Task SetSpeaking_AnnouncesToEveryMember()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        var sessionC = harness.Open(PeerC);
        sessionC.Receive(MessageType.RoomJoin, MemberList(room, PeerC));

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 3));

        await harness.Rooms.SetSpeakingAsync(true);

        Assert.Contains(sessionB.Sent, e => e.Type == MessageType.RoomSpeaking);
        Assert.Contains(sessionC.Sent, e => e.Type == MessageType.RoomSpeaking);
    }

    [Fact]
    public async Task RoomLeave_RemovesTheMemberAndTellsTheOthers()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        var sessionC = harness.Open(PeerC);
        sessionC.Receive(MessageType.RoomJoin, MemberList(room, PeerC));

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 3));

        sessionC.Receive(MessageType.RoomLeave, []);

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));
        Assert.DoesNotContain(harness.Rooms.Members, m => m.Id == PeerC);
        Assert.Contains(sessionB.Sent, e => e.Type == MessageType.RoomMemberList);
    }

    [Fact]
    public async Task ClosedSession_DropsTheMemberFromTheRoom()
    {
        await using var harness = new Harness();
        await harness.Rooms.StartAsync(CancellationToken.None);

        var room = harness.Rooms.CreateRoom("Sala");

        var sessionB = harness.Open(PeerB);
        sessionB.Receive(MessageType.RoomJoin, MemberList(room, PeerB));
        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 2));

        harness.Sessions.Close(PeerB);

        Assert.True(await WaitForAsync(() => harness.Rooms.Members.Count == 1));
        Assert.DoesNotContain(harness.Rooms.Members, m => m.Id == PeerB);
    }
}
