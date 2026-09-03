namespace LisoP2P.Media;

public interface IAudioEncoder : IDisposable
{
    byte[] Encode(AudioFrame frame);
}
