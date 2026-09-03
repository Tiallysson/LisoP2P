namespace LisoP2P.Media;

public sealed class AudioFrameAccumulator
{
    private readonly int _frameSamples;
    private readonly List<float> _pending;

    public AudioFrameAccumulator(int frameSamples)
    {
        if (frameSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSamples), "Frame size must be positive.");
        }

        _frameSamples = frameSamples;
        _pending = new List<float>(frameSamples * 4);
    }

    public int PendingSamples => _pending.Count;

    public IEnumerable<float[]> Add(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _pending.Add(sample);
        }

        var frames = new List<float[]>();

        while (_pending.Count >= _frameSamples)
        {
            var frame = new float[_frameSamples];
            _pending.CopyTo(0, frame, 0, _frameSamples);
            _pending.RemoveRange(0, _frameSamples);
            frames.Add(frame);
        }

        return frames;
    }

    public void Reset() => _pending.Clear();
}
