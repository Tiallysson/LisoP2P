namespace LisoP2P.Media;

public readonly record struct PreviewFrame(byte[] Bgra, int Width, int Height, int Stride);
