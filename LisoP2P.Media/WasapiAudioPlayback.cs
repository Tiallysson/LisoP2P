using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LisoP2P.Media;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    private const int LatencyMilliseconds = 60;

    private readonly object _sync = new();

    private WasapiOut? _output;
    private PullWaveProvider? _provider;
    private Func<float[]?>? _pull;
    private float _volume = 1f;

    public bool IsRunning { get; private set; }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);

            if (_provider is not null)
            {
                _provider.Volume = _volume;
            }
        }
    }

    public void SetSource(Func<float[]?> pullCallback)
    {
        _pull = pullCallback;

        if (_provider is not null)
        {
            _provider.Pull = pullCallback;
        }
    }

    public void Start(AudioSettings settings)
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            var device = ResolveDevice(settings.OutputDeviceId);

            _provider = new PullWaveProvider(settings.SampleRate, settings.Channels)
            {
                Pull = _pull,
                Volume = _volume,
            };

            _output = device is null
                ? new WasapiOut(AudioClientShareMode.Shared, true, LatencyMilliseconds)
                : new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMilliseconds);

            _output.Init(_provider);
            _output.Play();
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;

            try
            {
                _output?.Stop();
            }
            catch (Exception)
            {
            }

            _output?.Dispose();
            _output = null;
            _provider = null;
        }
    }

    private static MMDevice? ResolveDevice(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrEmpty(deviceId))
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (string.Equals(device.ID, deviceId, StringComparison.Ordinal))
                    {
                        return device;
                    }
                }
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => Stop();

    private sealed class PullWaveProvider : IWaveProvider
    {
        private readonly List<float> _remainder = [];

        public WaveFormat WaveFormat { get; }
        public Func<float[]?>? Pull { get; set; }
        public float Volume { get; set; } = 1f;

        public PullWaveProvider(int sampleRate, int channels)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public int Read(Span<byte> buffer)
        {
            var requested = buffer.Length / 4;

            while (_remainder.Count < requested)
            {
                var frame = Pull?.Invoke();

                if (frame is null || frame.Length == 0)
                {
                    break;
                }

                _remainder.AddRange(frame);
            }

            var available = Math.Min(requested, _remainder.Count);
            var gain = Volume;

            for (var i = 0; i < available; i++)
            {
                var value = Math.Clamp(_remainder[i] * gain, -1f, 1f);
                BitConverter.TryWriteBytes(buffer.Slice(i * 4, 4), value);
            }

            _remainder.RemoveRange(0, available);

            for (var i = available; i < requested; i++)
            {
                BitConverter.TryWriteBytes(buffer.Slice(i * 4, 4), 0f);
            }

            return requested * 4;
        }
    }
}
