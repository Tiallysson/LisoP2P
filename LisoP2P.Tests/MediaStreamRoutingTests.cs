using System.Net;
using System.Net.Sockets;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class MediaStreamRoutingTests
{
    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static byte[] Packet(byte streamId, uint frameId, byte[] payload, bool keyframe = false)
    {
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];

        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(
                MediaPacketCodec.CurrentVersion,
                streamId,
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
        await using var receiver = new UdpMediaReceiver();

        var audio = new List<(uint Sequence, byte[] Data)>();
        var video = new List<DecodableFrame>();

        receiver.AudioPacketReceived += (sequence, data) => audio.Add((sequence, data));
        receiver.FrameReassembled += frame => video.Add(frame);

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
        await using var receiver = new UdpMediaReceiver();

        var audio = new List<uint>();
        receiver.AudioPacketReceived += (sequence, _) => audio.Add(sequence);

        await receiver.StartAsync(port, CancellationToken.None);

        using var sender = new UdpMediaSender();
        var destination = new IPEndPoint(IPAddress.Loopback, port);

        sender.SendAudio([1, 2, 3], destination);
        sender.SendAudio([4, 5, 6], destination);
        sender.SendAudio([7, 8, 9], destination);

        Assert.True(await WaitForAsync(() => audio.Count == 3));
        Assert.Equal([0u, 1u, 2u], audio);
        Assert.Equal(3u, sender.AudioPacketsSent);
    }

    [Fact]
    public async Task AudioPacketsClaimingSeveralFragments_AreIgnored()
    {
        var port = FreePort();
        await using var receiver = new UdpMediaReceiver();

        var audio = 0;
        var video = 0;

        receiver.AudioPacketReceived += (_, _) => audio++;
        receiver.FrameReassembled += _ => video++;

        await receiver.StartAsync(port, CancellationToken.None);

        var payload = new byte[] { 1, 2 };
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];
        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(MediaPacketCodec.CurrentVersion, MediaPacketCodec.AudioStreamId, 1, 0, 4, 0, 2),
            payload);

        using var socket = new UdpClient();
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        socket.Send(buffer, buffer.Length, destination);

        var good = Packet(MediaPacketCodec.AudioStreamId, 2, [3]);
        socket.Send(good, good.Length, destination);

        Assert.True(await WaitForAsync(() => audio == 1));
        Assert.Equal(0, video);
    }
}
