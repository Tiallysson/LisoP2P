using Concentus;

namespace LisoP2P.Media;

public sealed class OpusAudioDecoder : IAudioDecoder
{
    private readonly IOpusDecoder _decoder;
    private readonly int _frameSamples;

    public OpusAudioDecoder(AudioSettings settings)
    {
        _frameSamples = settings.FrameSamples;
        _decoder = OpusCodecFactory.CreateDecoder(settings.SampleRate, settings.Channels);
    }

    public float[] Decode(byte[] opusData)
    {
        if (opusData.Length == 0)
        {
            return DecodePacketLoss();
        }

        var samples = new float[_frameSamples];

        try
        {
            var decoded = _decoder.Decode(opusData, samples, _frameSamples, false);

            return decoded == _frameSamples ? samples : samples.AsSpan(0, Math.Max(0, decoded)).ToArray();
        }
        catch (Exception)
        {
            return DecodePacketLoss();
        }
    }

    public float[] DecodePacketLoss()
    {
        var samples = new float[_frameSamples];

        try
        {
            _decoder.Decode(ReadOnlySpan<byte>.Empty, samples, _frameSamples, false);
        }
        catch (Exception)
        {
            Array.Clear(samples);
        }

        return samples;
    }

    public void Dispose() => _decoder.Dispose();
}
