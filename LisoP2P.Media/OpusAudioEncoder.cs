using Concentus;
using Concentus.Enums;

namespace LisoP2P.Media;

public sealed class OpusAudioEncoder : IAudioEncoder
{
    private const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private readonly int _frameSamples;
    private readonly byte[] _buffer = new byte[MaxPacketBytes];

    public OpusAudioEncoder(AudioSettings settings)
    {
        _frameSamples = settings.FrameSamples;
        _encoder = OpusCodecFactory.CreateEncoder(
            settings.SampleRate,
            settings.Channels,
            OpusApplication.OPUS_APPLICATION_VOIP);

        _encoder.Bitrate = settings.BitrateBps;
        _encoder.UseVBR = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
    }

    public byte[] Encode(AudioFrame frame)
    {
        if (frame.Samples.Length != _frameSamples)
        {
            throw new ArgumentException($"Opus expects exactly {_frameSamples} samples per frame.", nameof(frame));
        }

        var written = _encoder.Encode(frame.Samples, _frameSamples, _buffer, _buffer.Length);

        return _buffer.AsSpan(0, written).ToArray();
    }

    public void Dispose() => _encoder.Dispose();
}
