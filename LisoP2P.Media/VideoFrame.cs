namespace LisoP2P.Media;

public readonly record struct VideoFrame(byte[] Nv12, int Length, int Width, int Height, long TimestampTicks);
