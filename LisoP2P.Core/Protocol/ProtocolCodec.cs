using MessagePack;

namespace LisoP2P.Core.Protocol;

public static class ProtocolCodec
{
    /// <summary>
    /// Version 2 replaced the 16-byte Guid sender with the 32-byte Ed25519 public key of fase 6.
    /// A version-1 envelope no longer decodes: the id changed shape, not just size.
    /// </summary>
    public const byte CurrentVersion = 2;

    public static byte[] Encode(Envelope envelope) => MessagePackSerializer.Serialize(envelope);

    public static bool TryDecode(ReadOnlySpan<byte> data, out Envelope? envelope)
    {
        envelope = null;

        Envelope? decoded;

        try
        {
            decoded = MessagePackSerializer.Deserialize<Envelope>(data.ToArray());
        }
        catch
        {
            return false;
        }

        // MessagePack maps a bare nil onto a null object and onto null reference properties, so
        // nothing here can be assumed non-null just because the type declares an initializer.
        if (decoded is null
            || decoded.Version != CurrentVersion
            || !Enum.IsDefined(decoded.Type)
            || !PeerId.IsValidKey(decoded.SenderId))
        {
            return false;
        }

        envelope = decoded.Payload is null
            ? new Envelope
            {
                Version = decoded.Version,
                Type = decoded.Type,
                SenderId = decoded.SenderId,
                TimestampUnixMs = decoded.TimestampUnixMs,
            }
            : decoded;

        return true;
    }
}
