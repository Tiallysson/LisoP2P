using System.Buffers.Binary;
using LisoP2P.Core.Protocol;

namespace LisoP2P.Tests;

public class FrameTests
{
    private static Envelope SampleEnvelope() => new()
    {
        Version = ProtocolCodec.CurrentVersion,
        Type = MessageType.Ping,
        SenderId = Guid.NewGuid(),
        TimestampUnixMs = 123456,
        Payload = [1, 2, 3, 4, 5],
    };

    [Fact]
    public async Task WriteThenRead_PreservesEnvelope()
    {
        var envelope = SampleEnvelope();
        using var stream = new MemoryStream();

        await FrameWriter.WriteAsync(stream, envelope, CancellationToken.None);
        stream.Position = 0;

        var decoded = await FrameReader.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(envelope.Version, decoded!.Version);
        Assert.Equal(envelope.Type, decoded.Type);
        Assert.Equal(envelope.SenderId, decoded.SenderId);
        Assert.Equal(envelope.TimestampUnixMs, decoded.TimestampUnixMs);
        Assert.Equal(envelope.Payload, decoded.Payload);
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();

        var decoded = await FrameReader.ReadAsync(stream, CancellationToken.None);

        Assert.Null(decoded);
    }

    [Fact]
    public async Task ReadAsync_OversizedFramePrefix_ThrowsBeforeReadingBody()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, FrameReader.MaxFrameSize + 1);
        await stream.WriteAsync(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameReader.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_StreamDeliversOneByteAtATime_StillReconstructsFrame()
    {
        var envelope = SampleEnvelope();
        using var buffer = new MemoryStream();
        await FrameWriter.WriteAsync(buffer, envelope, CancellationToken.None);

        using var slowStream = new OneByteAtATimeStream(buffer.ToArray());
        var decoded = await FrameReader.ReadAsync(slowStream, CancellationToken.None);

        Assert.NotNull(decoded);
        Assert.Equal(envelope.Type, decoded!.Type);
        Assert.Equal(envelope.SenderId, decoded.SenderId);
        Assert.Equal(envelope.Payload, decoded.Payload);
    }

    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private readonly Queue<byte> _remaining = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining.Count == 0)
            {
                return 0;
            }

            buffer[offset] = _remaining.Dequeue();
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining.Count == 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[0] = _remaining.Dequeue();
            return ValueTask.FromResult(1);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
