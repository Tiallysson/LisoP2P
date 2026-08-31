using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class AnnouncePayloadCodec
{
    public static byte[] Encode(AnnouncePayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out AnnouncePayload? payload)
    {
        try
        {
            payload = MessagePackSerializer.Deserialize<AnnouncePayload>(data.ToArray());
            return true;
        }
        catch
        {
            payload = null;
            return false;
        }
    }
}
