using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class HelloPayloadCodec
{
    public const byte CurrentProtocolVersion = 1;

    public static byte[] Encode(HelloPayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out HelloPayload? payload)
    {
        try
        {
            payload = MessagePackSerializer.Deserialize<HelloPayload>(data.ToArray());
            return true;
        }
        catch
        {
            payload = null;
            return false;
        }
    }
}
