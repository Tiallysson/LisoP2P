using LisoP2P.Media;

namespace LisoP2P.Tests;

public class AudioFrameAccumulatorTests
{
    [Fact]
    public void Add_EmitsNothingUntilAFullFrameIsAvailable()
    {
        var accumulator = new AudioFrameAccumulator(960);

        Assert.Empty(accumulator.Add(new float[500]));
        Assert.Equal(500, accumulator.PendingSamples);

        var frames = accumulator.Add(new float[460]).ToList();

        Assert.Single(frames);
        Assert.Equal(960, frames[0].Length);
        Assert.Equal(0, accumulator.PendingSamples);
    }

    [Fact]
    public void Add_EmitsSeveralFramesFromOneLargeBuffer()
    {
        var accumulator = new AudioFrameAccumulator(960);

        var frames = accumulator.Add(new float[2000]).ToList();

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(960, frame.Length));
        Assert.Equal(80, accumulator.PendingSamples);
    }

    [Fact]
    public void Add_KeepsTheSampleOrderAcrossFrameBoundaries()
    {
        var accumulator = new AudioFrameAccumulator(4);
        var input = new[] { 1f, 2f, 3f, 4f, 5f, 6f };

        var frames = accumulator.Add(input).ToList();

        Assert.Single(frames);
        Assert.Equal([1f, 2f, 3f, 4f], frames[0]);

        var next = accumulator.Add([7f, 8f]).ToList();

        Assert.Single(next);
        Assert.Equal([5f, 6f, 7f, 8f], next[0]);
    }

    [Fact]
    public void Reset_DropsThePartialFrame()
    {
        var accumulator = new AudioFrameAccumulator(960);

        accumulator.Add(new float[500]);
        accumulator.Reset();

        Assert.Equal(0, accumulator.PendingSamples);
    }

    [Fact]
    public void Constructor_RejectsAFrameSizeOfZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFrameAccumulator(0));
    }
}
