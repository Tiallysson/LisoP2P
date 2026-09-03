namespace LisoP2P.Media;

public readonly record struct DecodableFrame(byte[] Data, bool IsKeyframe, uint FrameId);
