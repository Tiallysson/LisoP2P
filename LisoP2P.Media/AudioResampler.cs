namespace LisoP2P.Media;

public static class AudioResampler
{
    public static float[] ToMono(ReadOnlySpan<float> samples, int channels)
    {
        if (channels <= 1)
        {
            return samples.ToArray();
        }

        var frames = samples.Length / channels;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * channels;

            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[offset + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    public static float[] Resample(ReadOnlySpan<float> samples, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate), "Sample rates must be positive.");
        }

        if (sourceRate == targetRate || samples.Length == 0)
        {
            return samples.ToArray();
        }

        var ratio = (double)targetRate / sourceRate;
        var length = Math.Max(1, (int)(samples.Length * ratio));
        var resampled = new float[length];

        for (var i = 0; i < length; i++)
        {
            var position = i / ratio;
            var index = (int)position;
            var fraction = (float)(position - index);

            var first = samples[Math.Min(index, samples.Length - 1)];
            var second = samples[Math.Min(index + 1, samples.Length - 1)];

            resampled[i] = first + ((second - first) * fraction);
        }

        return resampled;
    }

    public static float[] Normalize(ReadOnlySpan<float> samples, int channels, int sourceRate, int targetRate) =>
        Resample(ToMono(samples, channels), sourceRate, targetRate);

    public static void MixInto(Span<float> destination, ReadOnlySpan<float> source)
    {
        var length = Math.Min(destination.Length, source.Length);

        for (var i = 0; i < length; i++)
        {
            destination[i] = Math.Clamp(destination[i] + source[i], -1f, 1f);
        }
    }
}
