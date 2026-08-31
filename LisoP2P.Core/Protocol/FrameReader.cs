using System.Buffers.Binary;

namespace LisoP2P.Core.Protocol;

public static class FrameReader
{
    public const int MaxFrameSize = 64 * 1024;

    public static async Task<Envelope?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameSize)
        {
            throw new InvalidDataException($"Frame size {length} exceeds MaxFrameSize ({MaxFrameSize}).");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

        if (!ProtocolCodec.TryDecode(body, out var envelope) || envelope is null)
        {
            throw new InvalidDataException("Malformed envelope.");
        }

        return envelope;
    }
}
