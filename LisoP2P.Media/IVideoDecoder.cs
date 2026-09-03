namespace LisoP2P.Media;

public interface IVideoDecoder : IDisposable
{
    string Name { get; }
    bool IsHardware { get; }
    int Width { get; }
    int Height { get; }

    void Configure(int width, int height);
    PreviewFrame? Decode(DecodableFrame frame);
}
