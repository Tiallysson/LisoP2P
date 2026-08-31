using LisoP2P.Core.Protocol;

namespace LisoP2P.Tests;

public class ProtocolCodecTests
{
    [Fact]
    public void EncodeThenDecode_PreservesAllFields()
    {
        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.Announce,
            SenderId = Guid.NewGuid(),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = [1, 2, 3, 4],
        };

        var encoded = ProtocolCodec.Encode(envelope);
        var decoded = ProtocolCodec.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.NotNull(result);
        Assert.Equal(envelope.Version, result.Version);
        Assert.Equal(envelope.Type, result.Type);
        Assert.Equal(envelope.SenderId, result.SenderId);
        Assert.Equal(envelope.TimestampUnixMs, result.TimestampUnixMs);
        Assert.Equal(envelope.Payload, result.Payload);
    }

    [Fact]
    public void TryDecode_EmptyArray_ReturnsFalse()
    {
        var decoded = ProtocolCodec.TryDecode([], out var result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_RandomBytes_ReturnsFalse()
    {
        var random = new byte[] { 0x42, 0x13, 0x99, 0xFF, 0x00, 0x7A };

        var decoded = ProtocolCodec.TryDecode(random, out var result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_UnknownVersion_ReturnsFalse()
    {
        var envelope = new Envelope
        {
            Version = 99,
            Type = MessageType.Announce,
            SenderId = Guid.NewGuid(),
            TimestampUnixMs = 0,
            Payload = [],
        };

        var decoded = ProtocolCodec.TryDecode(ProtocolCodec.Encode(envelope), out var result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecode_UnknownMessageType_ReturnsFalse()
    {
        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = (MessageType)255,
            SenderId = Guid.NewGuid(),
            TimestampUnixMs = 0,
            Payload = [],
        };

        var decoded = ProtocolCodec.TryDecode(ProtocolCodec.Encode(envelope), out var result);

        Assert.False(decoded);
        Assert.Null(result);
    }
}
