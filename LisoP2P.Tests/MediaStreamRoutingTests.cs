using System.Net;
using System.Net.Sockets;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class MediaStreamRoutingTests
{
    private static readonly Guid SelfId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");
    private static readonly Guid PeerAId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid PeerBId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static UdpMediaReceiver NewReceiver() =>
        new(new StubIdentityStore(SelfId, "self"));

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static byte[] Packet(byte streamId, uint frameId, byte[] payload, bool keyframe = false) =>
        Packet(PeerAId, streamId, frameId, payload, keyframe);

    private static byte[] Packet(Guid sender, byte streamId, uint frameId, byte[] payload, bool keyframe = false)
    {
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];

        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(
                MediaPacketCodec.CurrentVersion,
                streamId,
                sender,
                frameId,
                0,
                1,
                keyframe ? MediaPacketCodec.KeyframeFlag : (byte)0,
                (ushort)payload.Length),
            payload);

        return buffer;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task AudioPackets_GoToTheAudioPathAndVideoPacketsToReassembly()
    {
        var port = FreePort();
        await using var receiver = NewReceiver();

        var audio = new List<(uint Sequence, byte[] Data)>();
        var video = new List<DecodableFrame>();

        receiver.AudioPacketReceived += (_, sequence, data) => audio.Add((sequence, data));
        receiver.FrameReassembled += (_, frame) => video.Add(frame);

        await receiver.StartAsync(port, CancellationToken.None);

        using var socket = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        var audioPacket = Packet(MediaPacketCodec.AudioStreamId, 7, [9, 8, 7]);
        var videoPacket = Packet(MediaPacketCodec.VideoStreamId, 1, [1, 2, 3], keyframe: true);

        socket.Send(audioPacket, audioPacket.Length, destination);
        socket.Send(videoPacket, videoPacket.Length, destination);

        Assert.True(await WaitForAsync(() => audio.Count == 1 && video.Count == 1));

        Assert.Equal(7u, audio[0].Sequence);
        Assert.Equal([9, 8, 7], audio[0].Data);
        Assert.Equal([1, 2, 3], video[0].Data);
        Assert.True(video[0].IsKeyframe);
        Assert.Equal(0, receiver.PendingFrames);
    }

    [Fact]
    public async Task SentAudio_ArrivesWithAnIncrementingSequenceOnTheAudioStream()
    {
        var port = FreePort();
        await using var receiver = NewReceiver();

        var audio = new List<uint>();
        receiver.AudioPacketReceived += (_, sequence, _) => audio.Add(sequence);

        await receiver.StartAsync(port, CancellationToken.None);

        using var sender = new UdpMediaSender(new StubIdentityStore(PeerAId, "peer"));
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        sender.SendAudio([1, 2, 3], [destination]);
        sender.SendAudio([4, 5, 6], [destination]);
        sender.SendAudio([7, 8, 9], [destination]);

        Assert.True(await WaitForAsync(() => audio.Count == 3));
        Assert.Equal([0u, 1u, 2u], audio);
        Assert.Equal(3u, sender.AudioPacketsSent);
    }

    [Fact]
    public async Task AudioPacketsClaimingSeveralFragments_AreIgnored()
    {
        var port = FreePort();
        await using var receiver = NewReceiver();

        var audio = 0;
        var video = 0;

        receiver.AudioPacketReceived += (_, _, _) => audio++;
        receiver.FrameReassembled += (_, _) => video++;

        await receiver.StartAsync(port, CancellationToken.None);

        var payload = new byte[] { 1, 2 };
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];
        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(MediaPacketCodec.CurrentVersion, MediaPacketCodec.AudioStreamId, PeerAId, 1, 0, 4, 0, 2),
            payload);

        using var socket = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        socket.Send(buffer, buffer.Length, destination);

        var good = Packet(MediaPacketCodec.AudioStreamId, 2, [3]);
        socket.Send(good, good.Length, destination);

        Assert.True(await WaitForAsync(() => audio == 1));
        Assert.Equal(0, video);
    }

    [Fact]
    public async Task FramesFromTwoSenders_AreReassembledSeparately()
    {
        var port = FreePort();
        await using var receiver = NewReceiver();

        var frames = new List<(PeerId Sender, DecodableFrame Frame)>();
        receiver.FrameReassembled += (sender, frame) => frames.Add((sender, frame));

        await receiver.StartAsync(port, CancellationToken.None);

        using var socket = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        // Both senders use frame id 5 with two fragments each. Sharing one reassembler would splice
        // their fragments into a single corrupt frame.
        Send(socket, destination, Fragment(PeerAId, 5, 0, 2, [1, 1]));
        Send(socket, destination, Fragment(PeerBId, 5, 0, 2, [2, 2]));
        Send(socket, destination, Fragment(PeerBId, 5, 1, 2, [2, 2]));
        Send(socket, destination, Fragment(PeerAId, 5, 1, 2, [1, 1]));

        Assert.True(await WaitForAsync(() => frames.Count == 2));

        var fromA = Assert.Single(frames, entry => entry.Sender.Value == PeerAId);
        var fromB = Assert.Single(frames, entry => entry.Sender.Value == PeerBId);

        Assert.Equal([1, 1, 1, 1], fromA.Frame.Data);
        Assert.Equal([2, 2, 2, 2], fromB.Frame.Data);
    }

    [Fact]
    public async Task PacketsFromSelf_AreIgnored()
    {
        var port = FreePort();
        await using var receiver = NewReceiver();

        var seen = 0;
        receiver.AudioPacketReceived += (_, _, _) => seen++;

        await receiver.StartAsync(port, CancellationToken.None);

        using var socket = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        var own = Packet(SelfId, MediaPacketCodec.AudioStreamId, 1, [1]);
        socket.Send(own, own.Length, destination);

        var other = Packet(PeerAId, MediaPacketCodec.AudioStreamId, 2, [2]);
        socket.Send(other, other.Length, destination);

        Assert.True(await WaitForAsync(() => seen == 1));
    }

    private static void Send(UdpClient socket, IPEndPoint destination, byte[] packet) =>
        socket.Send(packet, packet.Length, destination);

    private static byte[] Fragment(Guid sender, uint frameId, ushort index, ushort count, byte[] payload)
    {
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];

        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(
                MediaPacketCodec.CurrentVersion,
                MediaPacketCodec.VideoStreamId,
                sender,
                frameId,
                index,
                count,
                MediaPacketCodec.KeyframeFlag,
                (ushort)payload.Length),
            payload);

        return buffer;
    }
}
