using LisoP2P.Core.Protocol;

namespace LisoP2P.Tests;

public class ScreenSharePayloadCodecTests
{
    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        var encoded = ScreenSharePayloadCodec.Encode(new ScreenSharePayload { Width = 1920, Height = 1080, Fps = 30 });

        Assert.True(ScreenSharePayloadCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(1920, decoded!.Width);
        Assert.Equal(1080, decoded.Height);
        Assert.Equal(30, decoded.Fps);
    }

    [Theory]
    [InlineData(0, 1080, 30)]
    [InlineData(1920, 0, 30)]
    [InlineData(1920, 1080, 0)]
    [InlineData(99999, 1080, 30)]
    [InlineData(1920, 1080, 9999)]
    public void TryDecode_RejectsOutOfRangeValues(int width, int height, int fps)
    {
        var encoded = ScreenSharePayloadCodec.Encode(new ScreenSharePayload { Width = width, Height = height, Fps = fps });

        Assert.False(ScreenSharePayloadCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalseOnGarbage()
    {
        Assert.False(ScreenSharePayloadCodec.TryDecode([0xFF, 0x00, 0x12], out _));
    }
}
