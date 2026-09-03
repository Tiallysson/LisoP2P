namespace LisoP2P.Media;

public sealed record VideoDecoderCandidate(string Name, bool IsHardware, Func<IVideoDecoder> Create);
