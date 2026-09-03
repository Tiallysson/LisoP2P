using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LisoP2P.Media;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    private const int MaxQueuedSecondaryFrames = 10;
    private const int CaptureBufferMilliseconds = 20;

    private readonly object _sync = new();
    private readonly ConcurrentQueue<float[]> _secondaryFrames = new();

    private WasapiCapture? _primary;
    private WasapiCapture? _secondary;
    private AudioFrameAccumulator? _primaryAccumulator;
    private AudioFrameAccumulator? _secondaryAccumulator;
    private AudioSettings _settings = new();
    private volatile bool _running;

    public bool IsRunning => _running;

    public event Action<AudioFrame>? FrameCaptured;
    public event Action<Exception>? Failed;

    public void Start(AudioSettings settings)
    {
        lock (_sync)
        {
            if (_running)
            {
                return;
            }

            _settings = settings;
            _primaryAccumulator = new AudioFrameAccumulator(settings.FrameSamples);
            _secondaryAccumulator = new AudioFrameAccumulator(settings.FrameSamples);
            _secondaryFrames.Clear();

            try
            {
                _primary = settings.Mode == AudioCaptureMode.SystemLoopback
                    ? CreateLoopbackCapture()
                    : CreateMicrophoneCapture(settings.InputDeviceId);

                _primary.DataAvailable += OnPrimaryData;
                _primary.RecordingStopped += OnRecordingStopped;

                if (settings.Mode == AudioCaptureMode.Both)
                {
                    _secondary = CreateLoopbackCapture();
                    _secondary.DataAvailable += OnSecondaryData;
                    _secondary.RecordingStopped += OnRecordingStopped;
                }

                _running = true;
                _primary.StartRecording();
                _secondary?.StartRecording();
            }
            catch (Exception ex)
            {
                _running = false;
                DisposeDevices();
                Failed?.Invoke(ex);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            try
            {
                _primary?.StopRecording();
                _secondary?.StopRecording();
            }
            catch (Exception)
            {
            }

            DisposeDevices();
            _secondaryFrames.Clear();
            _primaryAccumulator?.Reset();
            _secondaryAccumulator?.Reset();
        }
    }

    private static WasapiCapture CreateMicrophoneCapture(string? deviceId)
    {
        var device = ResolveDevice(deviceId, DataFlow.Capture);

        return device is null
            ? new WasapiCapture()
            : new WasapiCapture(device, true, CaptureBufferMilliseconds);
    }

    private static WasapiCapture CreateLoopbackCapture()
    {
        var device = ResolveDevice(null, DataFlow.Render);

        return device is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(device);
    }

    private static MMDevice? ResolveDevice(string? deviceId, DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrEmpty(deviceId))
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    if (string.Equals(device.ID, deviceId, StringComparison.Ordinal))
                    {
                        return device;
                    }
                }
            }

            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnPrimaryData(object? sender, WaveInEventArgs args)
    {
        if (!_running || _primaryAccumulator is null || _primary is null)
        {
            return;
        }

        var samples = Normalize(args, _primary.WaveFormat);

        foreach (var frame in _primaryAccumulator.Add(samples))
        {
            var mixed = frame;

            if (_settings.Mode == AudioCaptureMode.Both && _secondaryFrames.TryDequeue(out var secondary))
            {
                AudioResampler.MixInto(mixed, secondary);
            }

            FrameCaptured?.Invoke(new AudioFrame(mixed, DateTimeOffset.UtcNow.UtcTicks));
        }
    }

    private void OnSecondaryData(object? sender, WaveInEventArgs args)
    {
        if (!_running || _secondaryAccumulator is null || _secondary is null)
        {
            return;
        }

        var samples = Normalize(args, _secondary.WaveFormat);

        foreach (var frame in _secondaryAccumulator.Add(samples))
        {
            while (_secondaryFrames.Count >= MaxQueuedSecondaryFrames)
            {
                _secondaryFrames.TryDequeue(out _);
            }

            _secondaryFrames.Enqueue(frame);
        }
    }

    private float[] Normalize(WaveInEventArgs args, WaveFormat format)
    {
        var interleaved = ReadSamples(args.Buffer, args.BytesRecorded, format);

        return AudioResampler.Normalize(interleaved, format.Channels, format.SampleRate, _settings.SampleRate);
    }

    private static float[] ReadSamples(byte[] buffer, int count, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = new float[count / 4];

            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToSingle(buffer, i * 4);
            }

            return samples;
        }

        if (format.BitsPerSample == 16)
        {
            var samples = new float[count / 2];

            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2)) / 32768f;
            }

            return samples;
        }

        if (format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.Pcm)
        {
            var samples = new float[count / 4];

            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i * 4, 4)) / 2147483648f;
            }

            return samples;
        }

        return [];
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            Failed?.Invoke(args.Exception);
        }
    }

    private void DisposeDevices()
    {
        if (_primary is not null)
        {
            _primary.DataAvailable -= OnPrimaryData;
            _primary.RecordingStopped -= OnRecordingStopped;
            _primary.Dispose();
            _primary = null;
        }

        if (_secondary is not null)
        {
            _secondary.DataAvailable -= OnSecondaryData;
            _secondary.RecordingStopped -= OnRecordingStopped;
            _secondary.Dispose();
            _secondary = null;
        }
    }

    public void Dispose() => Stop();
}
