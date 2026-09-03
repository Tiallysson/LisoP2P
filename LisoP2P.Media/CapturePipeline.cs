using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace LisoP2P.Media;

public sealed class CapturePipeline : ICapturePipeline
{
    private readonly record struct PendingFrame(byte[] Buffer, int Length, int Width, int Height, long TimestampTicks);

    private readonly IScreenCapture _capture;
    private readonly IMediaLogger _logger;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private readonly object _sync = new();

    private LatestFrameSlot<PendingFrame>? _slot;
    private TextureConverter? _nv12;
    private TextureConverter? _preview;
    private IVideoEncoder? _encoder;
    private IEncodedFrameWriter? _recorder;
    private string? _recordPath;
    private bool _recorderFailed;
    private Thread? _encodeThread;
    private Timer? _statsTimer;
    private CaptureSettings _settings = new();
    private volatile bool _running;
    private int _statsTicks;
    private int _keyframeRequested;
    private int _capturedCount;
    private int _encodedCount;
    private long _encodedBytes;
    private long _previewTimestamp;
    private long _nextCaptureTimestamp;
    private int _initializeFailed;
    private byte[][] _previewBuffers = [];
    private int _previewBufferIndex;

    public IReadOnlyList<CaptureAdapterInfo> AvailableMonitors => _capture.AvailableMonitors;
    public bool IsRunning => _running;

    public event Action<EncodedFrame>? FrameReady;
    public event Action<CaptureStats>? StatsUpdated;
    public event Action<PreviewFrame>? PreviewReady;
    public event Action<string>? Log;

    public string? LogFilePath => _logger.FilePath;

    public string? LastRecordingPath { get; private set; }

    public CapturePipeline(IScreenCapture capture)
        : this(capture, NullMediaLogger.Instance)
    {
    }

    public CapturePipeline(IScreenCapture capture, IMediaLogger logger)
    {
        _capture = capture;
        _logger = logger;
    }

    public Task StartAsync(int monitorIndex, CaptureSettings settings, CancellationToken ct)
    {
        if (_running)
        {
            return Task.CompletedTask;
        }

        _settings = settings;
        _capturedCount = 0;
        _encodedCount = 0;
        _encodedBytes = 0;
        _keyframeRequested = 0;
        _initializeFailed = 0;
        _previewTimestamp = 0;
        _nextCaptureTimestamp = 0;
        _statsTicks = 0;
        _recorderFailed = false;
        _recordPath = settings.RecordToFile ? ResolveRecordPath(settings) : null;
        LastRecordingPath = null;
        _slot = new LatestFrameSlot<PendingFrame>(dropped => _pool.Return(dropped.Buffer));
        _running = true;

        WriteDiagnostics(monitorIndex, settings);

        _capture.FrameCaptured += OnFrameCaptured;
        _capture.Log += OnCaptureLog;
        _capture.Failed += OnCaptureFailed;

        _encodeThread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = "LisoP2P.VideoEncode",
        };
        _encodeThread.Start();

        _capture.Start(monitorIndex);
        _statsTimer = new Timer(_ => PublishStats(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    private void WriteDiagnostics(int monitorIndex, CaptureSettings settings)
    {
        _logger.Info(new string('-', 72));
        _logger.Info($"Iniciando captura: monitor={monitorIndex} fps={settings.TargetFps} altura={(settings.TargetHeight > 0 ? settings.TargetHeight.ToString() : "nativa")} bitrate={settings.TargetBitrateKbps}kbps gravar={settings.RecordToFile} forcarSoftware={settings.ForceSoftwareEncoder}");
        _logger.Info($"SO: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture}) processo={(Environment.Is64BitProcess ? "x64" : "x86")} runtime={RuntimeInformation.FrameworkDescription}");

        foreach (var line in _capture.DescribeAdapters())
        {
            _logger.Info(line);
        }

        foreach (var line in VideoEncoderFactory.DescribeAvailableEncoders())
        {
            _logger.Info(line);
        }
    }

    public Task StopAsync()
    {
        if (!_running)
        {
            return Task.CompletedTask;
        }

        _running = false;
        _logger.Info("Parando captura.");

        _capture.FrameCaptured -= OnFrameCaptured;
        _capture.Log -= OnCaptureLog;
        _capture.Failed -= OnCaptureFailed;
        _capture.Stop();

        _statsTimer?.Dispose();
        _statsTimer = null;

        _slot?.Close();
        _encodeThread?.Join(TimeSpan.FromSeconds(2));
        _encodeThread = null;
        _slot = null;

        lock (_sync)
        {
            if (_encoder is not null)
            {
                _encoder.FrameEncoded -= OnFrameEncoded;
                _encoder.Dispose();
                _encoder = null;
            }

            _recorder?.Dispose();
            _recorder = null;
            _nv12?.Dispose();
            _nv12 = null;
            _preview?.Dispose();
            _preview = null;
            _previewBuffers = [];
        }

        return Task.CompletedTask;
    }

    public void RequestKeyframe()
    {
        Interlocked.Exchange(ref _keyframeRequested, 1);
    }

    private void OnCaptureLog(string message)
    {
        _logger.Info(message);
        Log?.Invoke(message);
    }

    private void OnCaptureFailed(Exception error)
    {
        _logger.Error("Captura interrompida.", error);
        Log?.Invoke($"Captura interrompida: {error.Message}");
        _ = StopAsync();
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        if (!_running || _initializeFailed == 1)
        {
            return;
        }

        if (!ShouldProcess())
        {
            return;
        }

        if (!EnsureInitialized(frame))
        {
            return;
        }

        Interlocked.Increment(ref _capturedCount);

        var converter = _nv12!;
        var buffer = _pool.Rent(converter.BufferSize);

        if (!converter.TryConvert(frame.Texture, frame.TimestampTicks, buffer, out var readyTimestamp))
        {
            _pool.Return(buffer);
            PublishPreview(frame);
            return;
        }

        _slot?.Publish(new PendingFrame(buffer, converter.BufferSize, converter.Width, converter.Height, readyTimestamp));

        PublishPreview(frame);
    }

    private bool ShouldProcess()
    {
        if (_settings.TargetFps <= 0)
        {
            return true;
        }

        var now = Stopwatch.GetTimestamp();

        if (now < _nextCaptureTimestamp)
        {
            return false;
        }

        var interval = Stopwatch.Frequency / _settings.TargetFps;
        _nextCaptureTimestamp = Math.Max(now, _nextCaptureTimestamp + interval);
        return true;
    }

    private void PublishPreview(CapturedFrame frame)
    {
        var buffers = _previewBuffers;

        if (PreviewReady is null || _preview is null || buffers.Length == 0 || _settings.PreviewFps <= 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency / _settings.PreviewFps;

        if (now - _previewTimestamp < interval)
        {
            return;
        }

        _previewTimestamp = now;

        var buffer = buffers[_previewBufferIndex];
        _previewBufferIndex = (_previewBufferIndex + 1) % buffers.Length;

        if (_preview.TryConvert(frame.Texture, 0, buffer, out _))
        {
            PreviewReady.Invoke(new PreviewFrame(buffer, _preview.Width, _preview.Height, _preview.Width * 4));
        }
    }

    private bool EnsureInitialized(CapturedFrame frame)
    {
        if (_nv12 is not null && _encoder is not null)
        {
            return true;
        }

        lock (_sync)
        {
            if (_nv12 is not null && _encoder is not null)
            {
                return true;
            }

            try
            {
                var device = _capture.Device ?? throw new InvalidOperationException("Dispositivo Direct3D indisponível.");
                var context = _capture.Context ?? throw new InvalidOperationException("Contexto Direct3D indisponível.");

                var description = frame.Texture.Description;
                var (width, height) = ScaleToTarget((int)description.Width, (int)description.Height, _settings.TargetHeight);

                _nv12 = new TextureConverter(device, context, (int)description.Width, (int)description.Height, width, height, Format.NV12);

                var previewWidth = AlignEven(Math.Min(_settings.PreviewWidth, width));
                var previewHeight = AlignEven((int)(previewWidth * (double)height / width));
                _preview = new TextureConverter(
                    device,
                    context,
                    (int)description.Width,
                    (int)description.Height,
                    previewWidth,
                    previewHeight,
                    Format.B8G8R8A8_UNorm);

                _previewBuffers = [new byte[_preview.BufferSize], new byte[_preview.BufferSize], new byte[_preview.BufferSize]];
                _previewBufferIndex = 0;

                _encoder = VideoEncoderFactory.Create(
                    width,
                    height,
                    _settings.TargetBitrateKbps,
                    _settings.TargetFps,
                    message => Log?.Invoke(message),
                    _logger,
                    null,
                    !_settings.ForceSoftwareEncoder);

                _encoder.FrameEncoded += OnFrameEncoded;

                _logger.Info($"Pipeline inicializado: fonte={description.Width}x{description.Height} encode={width}x{height} preview={_preview.Width}x{_preview.Height}");
                _logger.Info("Encoder alimentado com NV12 em memoria de sistema; IMFDXGIDeviceManager nao e anexado para nao serializar o MFT contra o device de captura.");

                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _initializeFailed, 1);
                _logger.Error("Falha ao inicializar o pipeline.", ex);
                Log?.Invoke($"Falha ao inicializar o pipeline: {ex.Message}");
                return false;
            }
        }
    }

    private static string ResolveRecordPath(CaptureSettings settings)
    {
        var path = settings.RecordPath ?? CaptureSettings.DefaultRecordPath;
        var extension = settings.RecordFormat == RecordFormat.Mp4 ? ".mp4" : ".h264";

        return string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, extension);
    }

    private void EnsureRecorder(EncodedFrame frame)
    {
        if (_recorder is not null || _recorderFailed || _recordPath is null)
        {
            return;
        }

        try
        {
            if (_settings.RecordFormat == RecordFormat.AnnexB)
            {
                _recorder = AnnexBFileWriter.Create(_recordPath);
            }
            else
            {
                if (!frame.IsKeyframe)
                {
                    return;
                }

                var mediaType = _encoder?.CreateOutputMediaType();

                if (mediaType is null)
                {
                    _recorderFailed = true;
                    _logger.Error("Gravação MP4 indisponível: o encoder não expôs o tipo de saída.", new InvalidOperationException("CreateOutputMediaType retornou null."));
                    Log?.Invoke("Gravação MP4 indisponível: o encoder não expôs o tipo de saída.");
                    return;
                }

                _recorder = Mp4FileWriter.Create(_recordPath, mediaType, _settings.TargetFps);
            }

            LastRecordingPath = _recordPath;
            _logger.Info($"Gravando em {_recordPath}");
            Log?.Invoke($"Gravando em {_recordPath}");
        }
        catch (Exception ex)
        {
            _recorderFailed = true;
            _logger.Error("Falha ao iniciar a gravação.", ex);
            Log?.Invoke($"Falha ao iniciar a gravação: {ex.Message}");
        }
    }

    private static int AlignEven(int value) => value % 2 == 0 ? value : value - 1;

    public static (int Width, int Height) ScaleToTarget(int sourceWidth, int sourceHeight, int targetHeight)
    {
        if (targetHeight <= 0 || targetHeight >= sourceHeight)
        {
            return (AlignEven(sourceWidth), AlignEven(sourceHeight));
        }

        var width = (int)Math.Round(targetHeight * (double)sourceWidth / sourceHeight);
        return (AlignEven(width), AlignEven(targetHeight));
    }

    private void EncodeLoop()
    {
        while (_running)
        {
            var slot = _slot;
            if (slot is null)
            {
                break;
            }

            if (!slot.TryTake(100, out var pending))
            {
                continue;
            }

            try
            {
                var encoder = _encoder;
                if (encoder is null)
                {
                    continue;
                }

                var force = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
                var frame = new VideoFrame(pending.Buffer, pending.Length, pending.Width, pending.Height, pending.TimestampTicks);
                encoder.Encode(frame, force);
            }
            catch (Exception ex)
            {
                _logger.Error("Erro ao codificar.", ex);
                Log?.Invoke($"Erro ao codificar: {ex.Message}");
            }
            finally
            {
                _pool.Return(pending.Buffer);
            }
        }
    }

    private void OnFrameEncoded(EncodedFrame encoded)
    {
        Interlocked.Increment(ref _encodedCount);
        Interlocked.Add(ref _encodedBytes, encoded.Data.Length);

        try
        {
            lock (_sync)
            {
                EnsureRecorder(encoded);
                _recorder?.Write(encoded);
            }
        }
        catch (Exception ex)
        {
            _recorderFailed = true;
            _logger.Error("Erro ao gravar o arquivo.", ex);
            Log?.Invoke($"Erro ao gravar o arquivo: {ex.Message}");
        }

        FrameReady?.Invoke(encoded);
    }

    private void PublishStats()
    {
        var captured = Interlocked.Exchange(ref _capturedCount, 0);
        var encoded = Interlocked.Exchange(ref _encodedCount, 0);
        var bytes = Interlocked.Exchange(ref _encodedBytes, 0);
        var dropped = _slot?.DroppedCount ?? 0;
        var encoder = _encoder;

        var stats = new CaptureStats(
            captured,
            encoded,
            bytes * 8 / 1000.0,
            dropped,
            encoder?.Name ?? "-",
            encoder?.IsHardware ?? false);

        if (++_statsTicks % 10 == 1)
        {
            _logger.Info(
                $"stats captura={stats.CapturedFps:0}fps encode={stats.EncodedFps:0}fps " +
                $"bitrate={stats.BitrateKbps:0}kbps descartados={stats.DroppedFrames} encoder={stats.EncoderName}");
        }

        StatsUpdated?.Invoke(stats);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
