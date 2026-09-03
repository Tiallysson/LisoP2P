namespace LisoP2P.Media;

public readonly record struct EncodedFrame(
    byte[] Data,
    bool IsKeyframe,
    long TimestampTicks,
    long SampleTimeTicks = 0,
    long DurationTicks = 0);
