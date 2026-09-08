using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class RoomSpeakingPayloadCodec
{
    public static byte[] Encode(RoomSpeakingPayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out RoomSpeakingPayload? payload)
    {
        try
        {
            var decoded = MessagePackSerializer.Deserialize<RoomSpeakingPayload>(data.ToArray());

            if (decoded is null || decoded.RoomId == Guid.Empty)
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
