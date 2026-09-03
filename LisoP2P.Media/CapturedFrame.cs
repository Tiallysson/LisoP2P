using Vortice.Direct3D11;

namespace LisoP2P.Media;

public readonly record struct CapturedFrame(ID3D11Texture2D Texture, long TimestampTicks);
