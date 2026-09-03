namespace LisoP2P.Media;

public interface IEncodedFrameWriter : IDisposable
{
    long BytesWritten { get; }

    void Write(EncodedFrame frame);
}
