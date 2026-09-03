namespace LisoP2P.Media;

public interface IAudioPlayback : IDisposable
{
    bool IsRunning { get; }
    float Volume { get; set; }

    void Start(AudioSettings settings);
    void Stop();
    void SetSource(Func<float[]?> pullCallback);
}
