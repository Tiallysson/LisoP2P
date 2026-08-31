using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class Envelope
{
    [Key(0)] public byte Version { get; init; }
    [Key(1)] public MessageType Type { get; init; }
    [Key(2)] public Guid SenderId { get; init; }
    [Key(3)] public long TimestampUnixMs { get; init; }
    [Key(4)] public byte[] Payload { get; init; } = [];
}
