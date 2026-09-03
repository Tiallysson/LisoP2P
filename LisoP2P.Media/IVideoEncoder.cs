using Vortice.MediaFoundation;

namespace LisoP2P.Media;

public interface IVideoEncoder : IDisposable
{
    string Name { get; }
    bool IsHardware { get; }

    void Configure(int width, int height, int targetBitrateKbps, int fps);
    IMFMediaType? CreateOutputMediaType();
    EncodedFrame? Encode(VideoFrame frame, bool forceKeyframe);

    event Action<EncodedFrame> FrameEncoded;
}
