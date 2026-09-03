namespace LisoP2P.Media;

public readonly record struct CaptureStats(
    double CapturedFps,
    double EncodedFps,
    double BitrateKbps,
    int DroppedFrames,
    string EncoderName,
    bool IsHardwareEncoder);
