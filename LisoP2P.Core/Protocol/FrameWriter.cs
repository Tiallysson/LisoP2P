using System.Buffers.Binary;

namespace LisoP2P.Core.Protocol;

public static class FrameWriter
{
    public static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken ct)
    {
        var body = ProtocolCodec.Encode(envelope);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }
}
