using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class ChatMessagePayloadCodec
{
    public const int MaxTextLength = 4000;

    public static byte[] Encode(ChatMessagePayload payload)
    {
        if (payload.Text.Length > MaxTextLength)
        {
            throw new ArgumentException($"Text exceeds {MaxTextLength} characters.", nameof(payload));
        }

        return MessagePackSerializer.Serialize(payload);
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out ChatMessagePayload? payload)
    {
        try
        {
            var decoded = MessagePackSerializer.Deserialize<ChatMessagePayload>(data.ToArray());
            if (decoded.Text.Length > MaxTextLength)
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
