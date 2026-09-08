using LisoP2P.Net;

namespace LisoP2P.Tests;

public class BitrateLadderTests
{
    [Theory]
    [InlineData(0, 3000, 30)]
    [InlineData(1, 3000, 30)]
    [InlineData(2, 2200, 30)]
    [InlineData(3, 1600, 24)]
    [InlineData(4, 1000, 15)]
    [InlineData(9, 1000, 15)]
    public void SelectFor_ReturnsTheStepForTheReceiverCount(int receivers, int bitrate, int fps)
    {
        var step = BitrateLadder.SelectFor(receivers);

        Assert.Equal(bitrate, step.BitrateKbps);
        Assert.Equal(fps, step.Fps);
    }

    [Fact]
    public void SelectFor_NeverIncreasesQualityAsTheAudienceGrows()
    {
        var previous = BitrateLadder.SelectFor(1);

        for (var receivers = 2; receivers <= 8; receivers++)
        {
            var current = BitrateLadder.SelectFor(receivers);

            Assert.True(current.BitrateKbps <= previous.BitrateKbps);
            Assert.True(current.Fps <= previous.Fps);

            previous = current;
        }
    }
}
