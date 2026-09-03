namespace LisoP2P.Media;

public readonly record struct AudioFrame(float[] Samples, long TimestampTicks);
