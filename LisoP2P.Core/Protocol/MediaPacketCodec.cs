using System.Buffers.Binary;

namespace LisoP2P.Core.Protocol;

public static class MediaPacketCodec
{
    /// <summary>
    /// Version 2 added the 16-byte SenderId. In a mesh the receive socket takes datagrams from
    /// several senders at once, so the sender can no longer be inferred from "the only peer on the
    /// other end" — and inferring it from the source endpoint breaks when NAT or Radmin remaps the
    /// port. A version-1 packet no longer decodes: the field is not optional.
    /// </summary>
    public const byte CurrentVersion = 2;

    public const byte KeyframeFlag = 1;
    public const byte DefaultStreamId = 0;
    public const byte VideoStreamId = 0;
    public const byte AudioStreamId = 1;

    private const int SenderIdOffset = 2;
    private const int SenderIdSize = 16;
    private const int FrameIdOffset = SenderIdOffset + SenderIdSize;
    private const int FragmentIndexOffset = FrameIdOffset + 4;
    private const int FragmentCountOffset = FragmentIndexOffset + 2;
    private const int FlagsOffset = FragmentCountOffset + 2;
    private const int PayloadLengthOffset = FlagsOffset + 1;

    public const int HeaderSize = PayloadLengthOffset + 2;
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
        header.SenderId.TryWriteBytes(destination.Slice(SenderIdOffset, SenderIdSize));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[FrameIdOffset..], header.FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FragmentIndexOffset..], header.FragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[FragmentCountOffset..], header.FragmentCount);
        destination[FlagsOffset] = header.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[PayloadLengthOffset..], (ushort)payload.Length);
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

        var senderId = new Guid(packet.Slice(SenderIdOffset, SenderIdSize));

        if (senderId == Guid.Empty)
        {
            return false;
        }

        var fragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet[FragmentIndexOffset..]);
        var fragmentCount = BinaryPrimitives.ReadUInt16LittleEndian(packet[FragmentCountOffset..]);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[PayloadLengthOffset..]);

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
            senderId,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[FrameIdOffset..]),
            fragmentIndex,
            fragmentCount,
            packet[FlagsOffset],
            payloadLength);

        payload = packet.Slice(HeaderSize, payloadLength);
        return true;
    }
}
