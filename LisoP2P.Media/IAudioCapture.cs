namespace LisoP2P.Media;

public interface IAudioCapture : IDisposable
{
    bool IsRunning { get; }

    void Start(AudioSettings settings);
    void Stop();

    event Action<AudioFrame> FrameCaptured;
    event Action<Exception> Failed;
}
