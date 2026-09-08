using LisoP2P.Core;

namespace LisoP2P.Net;

public sealed class AudioMixer : IAudioMixer
{
    private readonly Func<PeerId, IJitterBuffer?> _resolveBuffer;

    private PeerId[] _sources = [];

    public AudioMixer(Func<PeerId, IJitterBuffer?> resolveBuffer) => _resolveBuffer = resolveBuffer;

    public void SetActiveSources(IReadOnlyCollection<PeerId> peers) =>
        Volatile.Write(ref _sources, [.. peers]);

    public float[]? MixNextFrame()
    {
        var sources = Volatile.Read(ref _sources);

        if (sources.Length == 0)
        {
            return null;
        }

        List<float[]>? contributions = null;

        foreach (var peer in sources)
        {
            // Every active buffer is pulled on every tick even when the mix already has audio: a
            // jitter buffer that is not drained on schedule drifts, and a gap has to become
            // concealment now rather than a wait.
            var frame = _resolveBuffer(peer)?.Pull();

            if (frame is null || frame.Length == 0)
            {
                continue;
            }

            (contributions ??= []).Add(frame);
        }

        if (contributions is null)
        {
            return null;
        }

        // A lone source is handed through untouched: it cannot overflow on its own, and Clamp
        // writes in place, which would mutate the decoder's own buffer.
        if (contributions.Count == 1)
        {
            return contributions[0];
        }

        var length = contributions.Max(frame => frame.Length);
        var mixed = new float[length];

        foreach (var frame in contributions)
        {
            for (var i = 0; i < frame.Length; i++)
            {
                mixed[i] += frame[i];
            }
        }

        // Dividing by the square root keeps one voice at roughly its own loudness instead of
        // halving everyone the moment a second person speaks; the clamp catches the peaks that
        // survive.
        var scale = 1f / MathF.Sqrt(contributions.Count);

        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] *= scale;
        }

        return Clamp(mixed);
    }

    private static float[] Clamp(float[] samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i], -1f, 1f);
        }

        return samples;
    }
}
