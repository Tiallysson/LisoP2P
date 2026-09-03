using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.Tests;

public class JitterBufferTests
{
    private sealed class MarkerDecoder : IAudioDecoder
    {
        public int ConcealmentCalls { get; private set; }

        public float[] Decode(byte[] opusData) => [opusData[0]];

        public float[] DecodePacketLoss()
        {
            ConcealmentCalls++;
            return [-1f];
        }

        public void Dispose()
        {
        }
    }

    private static byte[] Packet(byte marker) => [marker];

    private static JitterBuffer Create(MarkerDecoder decoder, int target = 3, int max = 7, int silence = 25) =>
        new(decoder, target, max, silence);

    [Fact]
    public void Pull_ReturnsNullUntilTheTargetDepthIsReached()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        buffer.Push(1, Packet(1), 0);
        Assert.Null(buffer.Pull());

        buffer.Push(2, Packet(2), 0);
        Assert.Null(buffer.Pull());

        buffer.Push(3, Packet(3), 0);
        Assert.Equal([1f], buffer.Pull());
    }

    [Fact]
    public void Pull_ReturnsFramesInOrderWithoutGaps()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        for (byte i = 1; i <= 5; i++)
        {
            buffer.Push(i, Packet(i), 0);
        }

        Assert.Equal([1f], buffer.Pull());
        Assert.Equal([2f], buffer.Pull());
        Assert.Equal([3f], buffer.Pull());
        Assert.Equal(0, decoder.ConcealmentCalls);
    }

    [Fact]
    public void Push_ReordersPacketsThatArriveOutOfOrder()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        buffer.Push(3, Packet(3), 0);
        buffer.Push(1, Packet(1), 0);
        buffer.Push(2, Packet(2), 0);

        Assert.Equal([1f], buffer.Pull());
        Assert.Equal([2f], buffer.Pull());
        Assert.Equal([3f], buffer.Pull());
        Assert.Equal(0, decoder.ConcealmentCalls);
    }

    [Fact]
    public void Pull_ConcealsAMissingPacketInsteadOfWaitingForIt()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        buffer.Push(1, Packet(1), 0);
        buffer.Push(3, Packet(3), 0);
        buffer.Push(4, Packet(4), 0);

        Assert.Equal([1f], buffer.Pull());
        Assert.Equal([-1f], buffer.Pull());
        Assert.Equal([3f], buffer.Pull());
        Assert.Equal(1, decoder.ConcealmentCalls);
        Assert.Equal(1, buffer.ConcealedFrames);
    }

    [Fact]
    public void Push_DiscardsPacketsThatArriveAfterTheirSlotWasPlayed()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        buffer.Push(10, Packet(10), 0);
        buffer.Push(11, Packet(11), 0);
        buffer.Push(12, Packet(12), 0);

        Assert.Equal([10f], buffer.Pull());

        buffer.Push(9, Packet(9), 0);

        Assert.Equal(1, buffer.LateDiscards);
        Assert.Equal([11f], buffer.Pull());
    }

    [Fact]
    public void Push_NeverGrowsBeyondTheMaximumDepth()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder, target: 3, max: 7);

        for (uint i = 1; i <= 200; i++)
        {
            buffer.Push(i, Packet((byte)(i % 250)), 0);
            Assert.True(buffer.Depth <= 7, $"Depth grew to {buffer.Depth}.");
        }

        Assert.True(buffer.OverflowDiscards > 0);
    }

    [Fact]
    public void Pull_SkipsAheadInsteadOfPlayingTheBacklogAfterAnOverflow()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder, target: 3, max: 7);

        for (byte i = 1; i <= 3; i++)
        {
            buffer.Push(i, Packet(i), 0);
        }

        Assert.Equal([1f], buffer.Pull());

        for (byte i = 4; i <= 40; i++)
        {
            buffer.Push(i, Packet(i), 0);
        }

        var next = buffer.Pull();

        Assert.NotNull(next);
        Assert.True(next![0] > 30f, $"Playback resumed at {next[0]} instead of skipping the backlog.");
    }

    [Fact]
    public void Pull_GoesSilentInsteadOfConcealingForeverWhenTheStreamStops()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder, target: 3, max: 7, silence: 5);

        for (byte i = 1; i <= 3; i++)
        {
            buffer.Push(i, Packet(i), 0);
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(buffer.Pull());
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal([-1f], buffer.Pull());
        }

        Assert.Null(buffer.Pull());
    }

    [Fact]
    public void Reset_ReturnsToPrebufferingWithoutThrowing()
    {
        var decoder = new MarkerDecoder();
        var buffer = Create(decoder);

        for (byte i = 1; i <= 4; i++)
        {
            buffer.Push(i, Packet(i), 0);
        }

        buffer.Pull();
        buffer.Reset();

        Assert.Equal(0, buffer.Depth);
        Assert.Null(buffer.Pull());
    }
}
