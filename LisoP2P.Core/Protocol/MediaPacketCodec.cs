using System.Buffers.Binary;

namespace LisoP2P.Core.Protocol;

public static class MediaPacketCodec
{
    public const byte CurrentVersion = 1;
    public const byte KeyframeFlag = 1;
    public const byte DefaultStreamId = 0;

    public const int HeaderSize = 13;
    public const int MaxPayloadSize = 1200;
    public const int MaxPacketSize = HeaderSize + MaxPayloadSize;

    public static int Encode(Span<byte> destination, in MediaPacketHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"Payload exceeds {ushort.MaxValue} bytes.", nameof(payload));
        }

        var total = HeaderSize + payload.Length;

        if (destination.Length < total)
        {
            throw new ArgumentException($"Destination needs at least {total} bytes.", nameof(destination));
        }

        destination[0] = header.Version;
        destination[1] = header.StreamId;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[2..], header.FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], header.FragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], header.FragmentCount);
        destination[10] = header.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[11..], (ushort)payload.Length);
        payload.CopyTo(destination[HeaderSize..]);

        return total;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out MediaPacketHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;

        if (packet.Length < HeaderSize)
        {
            return false;
        }

        var version = packet[0];

        if (version != CurrentVersion)
        {
            return false;
        }

        var fragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        var fragmentCount = BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[11..]);

        if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
        {
            return false;
        }

        if (payloadLength == 0 || payloadLength != packet.Length - HeaderSize)
        {
            return false;
        }

        header = new MediaPacketHeader(
            version,
            packet[1],
            BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]),
            fragmentIndex,
            fragmentCount,
            packet[10],
            payloadLength);

        payload = packet.Slice(HeaderSize, payloadLength);
        return true;
    }
}
