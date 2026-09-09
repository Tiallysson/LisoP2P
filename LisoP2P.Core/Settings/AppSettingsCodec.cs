using MessagePack;

namespace LisoP2P.Core.Settings;

public static class AppSettingsCodec
{
    public static byte[] Encode(AppSettings settings) => MessagePackSerializer.Serialize(settings);

    public static bool TryDecode(ReadOnlySpan<byte> data, out AppSettings? settings)
    {
        settings = null;

        try
        {
            var decoded = MessagePackSerializer.Deserialize<AppSettings>(data.ToArray());

            if (decoded is null)
            {
                return false;
            }

            settings = decoded.Normalized();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
