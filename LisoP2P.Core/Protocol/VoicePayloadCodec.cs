using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class VoicePayloadCodec
{
    public const int MaxSampleRate = 192000;
    public const int MaxChannels = 2;
    public const int MaxFrameSamples = 9600;

    public static byte[] Encode(VoicePayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out VoicePayload? payload)
    {
        try
        {
            var decoded = MessagePackSerializer.Deserialize<VoicePayload>(data.ToArray());

            if (decoded.SampleRate is <= 0 or > MaxSampleRate ||
                decoded.Channels is <= 0 or > MaxChannels ||
                decoded.FrameSamples is <= 0 or > MaxFrameSamples)
            {
                payload = null;
                return false;
            }

            payload = decoded;
            return true;
        }
        catch
        {
            payload = null;
            return false;
        }
    }
}
