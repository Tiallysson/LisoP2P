namespace LisoP2P.Media;

public sealed record VideoEncoderCandidate(string Name, bool IsHardware, Func<IVideoEncoder> Create);
