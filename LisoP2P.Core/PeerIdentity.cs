namespace LisoP2P.Core;

public sealed class PeerIdentity
{
    public required byte[] PublicKey { get; init; }
    public required string Fingerprint { get; init; }
    public required string Nickname { get; set; }

    public PeerId Id => new(PublicKey);
}
