using MessagePack;

namespace LisoP2P.Core.Protocol;

[MessagePackObject]
public sealed class Envelope
{
    [Key(0)] public byte Version { get; init; }
    [Key(1)] public MessageType Type { get; init; }

    /// <summary>The sender's Ed25519 public key — see <see cref="PeerId"/>.</summary>
    [Key(2)] public byte[] SenderId { get; init; } = [];

    [Key(3)] public long TimestampUnixMs { get; init; }
    [Key(4)] public byte[] Payload { get; init; } = [];
}
