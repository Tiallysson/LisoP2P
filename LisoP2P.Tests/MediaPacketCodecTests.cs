using LisoP2P.Core.Protocol;

namespace LisoP2P.Tests;

public class MediaPacketCodecTests
{
    [Fact]
    public void Encode_Decode_RoundTripsHeaderAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var header = new MediaPacketHeader(
            MediaPacketCodec.CurrentVersion,
            MediaPacketCodec.DefaultStreamId,
            4242,
            3,
            9,
            MediaPacketCodec.KeyframeFlag,
            (ushort)payload.Length);

        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];
        var written = MediaPacketCodec.Encode(buffer, header, payload);

        Assert.Equal(buffer.Length, written);
        Assert.True(MediaPacketCodec.TryDecode(buffer, out var decoded, out var decodedPayload));
        Assert.Equal(header, decoded);
        Assert.True(decoded.IsKeyframe);
        Assert.Equal(payload, decodedPayload.ToArray());
    }

    [Fact]
    public void TryDecode_RejectsPayloadLengthThatDoesNotMatchTheBytesReceived()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];
        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(MediaPacketCodec.CurrentVersion, 0, 1, 0, 1, 0, (ushort)payload.Length),
            payload);

        Assert.False(MediaPacketCodec.TryDecode(buffer.AsSpan(0, buffer.Length - 1), out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsFragmentIndexOutsideFragmentCount()
    {
        var payload = new byte[] { 7 };
        var buffer = new byte[MediaPacketCodec.HeaderSize + payload.Length];
        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(MediaPacketCodec.CurrentVersion, 0, 1, 4, 4, 0, (ushort)payload.Length),
            payload);

        Assert.False(MediaPacketCodec.TryDecode(buffer, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void TryDecode_RejectsShortPackets(int length)
    {
        Assert.False(MediaPacketCodec.TryDecode(new byte[length], out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsUnknownVersion()
    {
        var buffer = new byte[MediaPacketCodec.HeaderSize + 1];
        MediaPacketCodec.Encode(
            buffer,
            new MediaPacketHeader(MediaPacketCodec.CurrentVersion, 0, 1, 0, 1, 0, 1),
            [9]);
        buffer[0] = 200;

        Assert.False(MediaPacketCodec.TryDecode(buffer, out _, out _));
    }

    [Fact]
    public void TryDecode_NeverThrowsOnRandomBytes()
    {
        var random = new Random(1234);
        var buffer = new byte[64];

        for (var i = 0; i < 5000; i++)
        {
            random.NextBytes(buffer);
            MediaPacketCodec.TryDecode(buffer.AsSpan(0, random.Next(buffer.Length + 1)), out _, out _);
        }
    }
}
