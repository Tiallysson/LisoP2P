using LisoP2P.Media;

namespace LisoP2P.Tests;

public class AudioResamplerTests
{
    [Fact]
    public void ToMono_AveragesInterleavedChannels()
    {
        var stereo = new[] { 1f, 0f, 0.5f, -0.5f, -1f, 1f };

        var mono = AudioResampler.ToMono(stereo, 2);

        Assert.Equal([0.5f, 0f, 0f], mono);
    }

    [Fact]
    public void ToMono_LeavesMonoUntouched()
    {
        var samples = new[] { 0.1f, 0.2f, 0.3f };

        Assert.Equal(samples, AudioResampler.ToMono(samples, 1));
    }

    [Fact]
    public void Resample_ReturnsTheSameSamplesWhenRatesMatch()
    {
        var samples = new[] { 0.1f, 0.2f, 0.3f };

        Assert.Equal(samples, AudioResampler.Resample(samples, 48000, 48000));
    }

    [Fact]
    public void Resample_HalvesTheLengthWhenHalvingTheRate()
    {
        var samples = new float[960];

        var resampled = AudioResampler.Resample(samples, 48000, 24000);

        Assert.Equal(480, resampled.Length);
    }

    [Fact]
    public void Resample_DoublesTheLengthWhenDoublingTheRate()
    {
        var samples = new float[441];

        var resampled = AudioResampler.Resample(samples, 44100, 48000);

        Assert.Equal(480, resampled.Length);
    }

    [Fact]
    public void Resample_KeepsAConstantSignalConstant()
    {
        var samples = Enumerable.Repeat(0.25f, 441).ToArray();

        var resampled = AudioResampler.Resample(samples, 44100, 48000);

        Assert.All(resampled, sample => Assert.InRange(sample, 0.24f, 0.26f));
    }

    [Fact]
    public void MixInto_SumsAndClamps()
    {
        var destination = new[] { 0.5f, 0.9f, -0.9f };

        AudioResampler.MixInto(destination, [0.25f, 0.5f, -0.5f]);

        Assert.Equal(0.75f, destination[0], 3);
        Assert.Equal(1f, destination[1]);
        Assert.Equal(-1f, destination[2]);
    }

    [Fact]
    public void MixInto_StopsAtTheShorterBuffer()
    {
        var destination = new[] { 0.1f, 0.2f };

        AudioResampler.MixInto(destination, [0.1f]);

        Assert.Equal(0.2f, destination[0], 3);
        Assert.Equal(0.2f, destination[1], 3);
    }
}
