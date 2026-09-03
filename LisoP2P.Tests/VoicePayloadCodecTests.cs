using LisoP2P.Core.Protocol;

namespace LisoP2P.Tests;

public class VoicePayloadCodecTests
{
    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        var encoded = VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = 48000,
            Channels = 1,
            FrameSamples = 960,
        });

        Assert.True(VoicePayloadCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(48000, decoded!.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(960, decoded.FrameSamples);
    }

    [Theory]
    [InlineData(0, 1, 960)]
    [InlineData(48000, 0, 960)]
    [InlineData(48000, 1, 0)]
    [InlineData(48000, 8, 960)]
    [InlineData(48000, 1, 99999)]
    public void TryDecode_RejectsOutOfRangeValues(int sampleRate, int channels, int frameSamples)
    {
        var encoded = VoicePayloadCodec.Encode(new VoicePayload
        {
            SampleRate = sampleRate,
            Channels = channels,
            FrameSamples = frameSamples,
        });

        Assert.False(VoicePayloadCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void TryDecode_ReturnsFalseOnGarbage()
    {
        Assert.False(VoicePayloadCodec.TryDecode([0xFF, 0x41, 0x00], out _));
    }
}
