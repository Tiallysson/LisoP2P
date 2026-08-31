using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class ChatAckPayloadCodec
{
    public static byte[] Encode(ChatAckPayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out ChatAckPayload? payload)
    {
        try
        {
            payload = MessagePackSerializer.Deserialize<ChatAckPayload>(data.ToArray());
            return true;
        }
        catch
        {
            payload = null;
            return false;
        }
    }
}
