using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class FrameReassemblerTests
{
    private static MediaPacketHeader Header(uint frameId, int index, int count, bool keyframe = false) =>
        new(
            MediaPacketCodec.CurrentVersion,
            MediaPacketCodec.DefaultStreamId,
            frameId,
            (ushort)index,
            (ushort)count,
            keyframe ? MediaPacketCodec.KeyframeFlag : (byte)0,
            1);

    private static byte[][] Split(byte[] data, int fragmentCount)
    {
        var size = (data.Length + fragmentCount - 1) / fragmentCount;
        var fragments = new byte[fragmentCount][];

        for (var i = 0; i < fragmentCount; i++)
        {
            var offset = i * size;
            fragments[i] = data.AsSpan(offset, Math.Min(size, data.Length - offset)).ToArray();
        }

        return fragments;
    }

    private static void Feed(FrameReassembler reassembler, uint frameId, byte[][] fragments, IEnumerable<int> order, bool keyframe = false)
    {
        foreach (var index in order)
        {
            var header = new MediaPacketHeader(
                MediaPacketCodec.CurrentVersion,
                MediaPacketCodec.DefaultStreamId,
                frameId,
                (ushort)index,
                (ushort)fragments.Length,
                keyframe ? MediaPacketCodec.KeyframeFlag : (byte)0,
                (ushort)fragments[index].Length);

            reassembler.Add(header, fragments[index]);
        }
    }

    [Fact]
    public void Add_InOrderFragments_ReassemblesTheOriginalFrame()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var fragments = Split(data, 4);

        var reassembler = new FrameReassembler();
        DecodableFrame? completed = null;
        reassembler.FrameReassembled += frame => completed = frame;

        Feed(reassembler, 7, fragments, [0, 1, 2, 3], keyframe: true);

        Assert.NotNull(completed);
        Assert.Equal(data, completed!.Value.Data);
        Assert.True(completed.Value.IsKeyframe);
        Assert.Equal(7u, completed.Value.FrameId);
        Assert.Equal(0, reassembler.PendingCount);
    }

    [Fact]
    public void Add_OutOfOrderFragments_ReassemblesTheSameFrame()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var fragments = Split(data, 4);

        var reassembler = new FrameReassembler();
        DecodableFrame? completed = null;
        reassembler.FrameReassembled += frame => completed = frame;

        Feed(reassembler, 7, fragments, [3, 0, 2, 1]);

        Assert.NotNull(completed);
        Assert.Equal(data, completed!.Value.Data);
    }

    [Fact]
    public void CollectExpired_MissingFragment_DropsTheFrameAfterTheTimeout()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var reassembler = new FrameReassembler(() => now, timeout: TimeSpan.FromMilliseconds(200));

        var drops = 0;
        reassembler.FrameDropped += () => drops++;

        var fragments = Split(new byte[300], 4);
        Feed(reassembler, 1, fragments, [0, 1, 3]);

        now += TimeSpan.FromMilliseconds(150);
        reassembler.CollectExpired();
        Assert.Equal(0, drops);
        Assert.Equal(1, reassembler.PendingCount);

        now += TimeSpan.FromMilliseconds(100);
        reassembler.CollectExpired();

        Assert.Equal(1, drops);
        Assert.Equal(0, reassembler.PendingCount);
    }

    [Fact]
    public void Add_LateFragmentOfAlreadyDecidedFrame_IsIgnored()
    {
        var reassembler = new FrameReassembler();
        var completed = new List<uint>();
        reassembler.FrameReassembled += frame => completed.Add(frame.FrameId);

        Feed(reassembler, 105, Split(new byte[10], 1), [0]);
        Feed(reassembler, 103, Split(new byte[10], 1), [0]);

        Assert.Equal([105u], completed);
        Assert.Equal(0, reassembler.PendingCount);
    }

    [Fact]
    public void Add_NeverKeepsMoreFramesInFlightThanTheConfiguredWindow()
    {
        var reassembler = new FrameReassembler(windowSize: 8);

        for (uint frameId = 1; frameId <= 20; frameId++)
        {
            var fragments = Split(new byte[300], 4);
            Feed(reassembler, frameId, fragments, [0, 2]);

            Assert.True(reassembler.PendingCount <= 8);
        }

        Assert.True(reassembler.PendingCount <= 8);
    }

    [Fact]
    public void Add_DuplicateFragment_DoesNotCorruptTheFrame()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var fragments = Split(data, 3);

        var reassembler = new FrameReassembler();
        DecodableFrame? completed = null;
        reassembler.FrameReassembled += frame => completed = frame;

        Feed(reassembler, 4, fragments, [0, 0, 1, 2]);

        Assert.NotNull(completed);
        Assert.Equal(data, completed!.Value.Data);
    }

    [Fact]
    public void Add_FragmentWithMismatchedFragmentCount_IsDiscardedWithoutBreakingTheFrame()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var fragments = Split(data, 3);

        var reassembler = new FrameReassembler();
        DecodableFrame? completed = null;
        reassembler.FrameReassembled += frame => completed = frame;

        Feed(reassembler, 9, fragments, [0]);
        reassembler.Add(Header(9, 0, 7), new byte[] { 0xFF });
        Feed(reassembler, 9, fragments, [1, 2]);

        Assert.NotNull(completed);
        Assert.Equal(data, completed!.Value.Data);
    }

    [Fact]
    public void Reset_ClearsPendingFramesAndTheDeliveredWatermark()
    {
        var reassembler = new FrameReassembler();
        var completed = new List<uint>();
        reassembler.FrameReassembled += frame => completed.Add(frame.FrameId);

        Feed(reassembler, 100, Split(new byte[10], 1), [0]);
        reassembler.Reset();
        Feed(reassembler, 3, Split(new byte[10], 1), [0]);

        Assert.Equal([100u, 3u], completed);
    }
}
