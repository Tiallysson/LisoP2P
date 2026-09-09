namespace LisoP2P.Core.Protocol;

public readonly record struct MediaPacketHeader(
    byte Version,
    byte StreamId,
    PeerId SenderId,
    uint FrameId,
    ushort FragmentIndex,
    ushort FragmentCount,
    byte Flags,
    ushort PayloadLength)
{
    public bool IsKeyframe => (Flags & MediaPacketCodec.KeyframeFlag) != 0;
}
