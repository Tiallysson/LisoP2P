using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class ProtocolCodec
{
    public const byte CurrentVersion = 1;

    public static byte[] Encode(Envelope envelope) => MessagePackSerializer.Serialize(envelope);

    public static bool TryDecode(ReadOnlySpan<byte> data, out Envelope? envelope)
    {
        try
        {
            var decoded = MessagePackSerializer.Deserialize<Envelope>(data.ToArray());
            if (decoded.Version != CurrentVersion || !Enum.IsDefined(decoded.Type))
            {
                envelope = null;
                return false;
            }

            envelope = decoded;
            return true;
        }
        catch
        {
            envelope = null;
            return false;
        }
    }
}
