using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class ScreenSharePayloadCodec
{
    public const int MaxDimension = 8192;
    public const int MaxFps = 240;

    public static byte[] Encode(ScreenSharePayload payload) => MessagePackSerializer.Serialize(payload);

    public static bool TryDecode(ReadOnlySpan<byte> data, out ScreenSharePayload? payload)
    {
        try
        {
            var decoded = MessagePackSerializer.Deserialize<ScreenSharePayload>(data.ToArray());

            if (decoded.Width is <= 0 or > MaxDimension ||
                decoded.Height is <= 0 or > MaxDimension ||
                decoded.Fps is <= 0 or > MaxFps)
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
