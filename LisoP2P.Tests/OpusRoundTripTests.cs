using LisoP2P.Media;

namespace LisoP2P.Tests;

public class OpusRoundTripTests
{
    private static readonly AudioSettings Settings = new();

    private static AudioFrame CreateTone(int index)
    {
        var samples = new float[Settings.FrameSamples];

        for (var i = 0; i < samples.Length; i++)
        {
            var t = ((index * samples.Length) + i) / (double)Settings.SampleRate;
            samples[i] = (float)(Math.Sin(2 * Math.PI * 440 * t) * 0.5);
        }

        return new AudioFrame(samples, 0);
    }

    [Fact]
    public void EncodeDecode_ProducesAFrameOfTheSameLength()
    {
        using var encoder = new OpusAudioEncoder(Settings);
        using var decoder = new OpusAudioDecoder(Settings);

        float[]? decoded = null;

        for (var i = 0; i < 5; i++)
        {
            var packet = encoder.Encode(CreateTone(i));

            Assert.NotEmpty(packet);
            Assert.True(packet.Length < 400);

            decoded = decoder.Decode(packet);
        }

        Assert.NotNull(decoded);
        Assert.Equal(Settings.FrameSamples, decoded!.Length);
    }

    [Fact]
    public void EncodeDecode_KeepsTheSignalEnergy()
    {
        using var encoder = new OpusAudioEncoder(Settings);
        using var decoder = new OpusAudioDecoder(Settings);

        float[] decoded = [];

        for (var i = 0; i < 10; i++)
        {
            decoded = decoder.Decode(encoder.Encode(CreateTone(i)));
        }

        var energy = decoded.Sum(sample => Math.Abs(sample)) / decoded.Length;

        Assert.True(energy > 0.05, $"Decoded energy was {energy}.");
    }

    [Fact]
    public void Encode_RejectsAFrameOfTheWrongSize()
    {
        using var encoder = new OpusAudioEncoder(Settings);

        Assert.Throws<ArgumentException>(() => encoder.Encode(new AudioFrame(new float[100], 0)));
    }

    [Fact]
    public void DecodePacketLoss_ReturnsAFullFrameInsteadOfThrowing()
    {
        using var encoder = new OpusAudioEncoder(Settings);
        using var decoder = new OpusAudioDecoder(Settings);

        decoder.Decode(encoder.Encode(CreateTone(0)));

        var concealed = decoder.DecodePacketLoss();

        Assert.Equal(Settings.FrameSamples, concealed.Length);
    }

    [Fact]
    public void Decode_FallsBackToConcealmentOnGarbage()
    {
        using var decoder = new OpusAudioDecoder(Settings);

        var decoded = decoder.Decode([0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.Equal(Settings.FrameSamples, decoded.Length);
    }
}
