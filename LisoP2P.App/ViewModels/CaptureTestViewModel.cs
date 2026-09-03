using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Media;

namespace LisoP2P.App.ViewModels;

public sealed partial class CaptureTestViewModel : ObservableObject
{
    private readonly ICapturePipeline _pipeline;
    private WriteableBitmap? _bitmap;
    private int _previewPending;

    public static IReadOnlyList<ResolutionOption> ResolutionOptions { get; } =
    [
        new ResolutionOption("720p", 720),
        new ResolutionOption("1080p", 1080),
        new ResolutionOption("Nativa", 0),
    ];

    public static IReadOnlyList<int> FpsOptions { get; } = [30, 45, 60];

    public static IReadOnlyList<PreviewOption> PreviewOptions { get; } =
    [
        new PreviewOption("Baixa (640p)", 640),
        new PreviewOption("Média (1280p)", 1280),
        new PreviewOption("Alta (1920p)", 1920),
    ];

    public ObservableCollection<CaptureAdapterInfo> Monitors { get; } = [];
    public ObservableCollection<string> LogMessages { get; } = [];

    [ObservableProperty]
    private CaptureAdapterInfo? _selectedMonitor;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _recordToFile;

    [ObservableProperty]
    private bool _forceSoftwareEncoder;

    [ObservableProperty]
    private int _targetFps = 30;

    [ObservableProperty]
    private int _bitrateKbps = 5000;

    [ObservableProperty]
    private ResolutionOption _selectedResolution = ResolutionOptions[1];

    [ObservableProperty]
    private PreviewOption _selectedPreview = PreviewOptions[1];

    [ObservableProperty]
    private string _statsText = "Sem captura ativa";

    [ObservableProperty]
    private string _encoderText = "Encoder: -";

    [ObservableProperty]
    private ImageSource? _preview;

    public string LogFileText => _pipeline.LogFilePath is null
        ? "Log em arquivo desabilitado"
        : $"Log: {_pipeline.LogFilePath}";

    public CaptureTestViewModel(ICapturePipeline pipeline)
    {
        _pipeline = pipeline;
        _pipeline.StatsUpdated += OnStatsUpdated;
        _pipeline.PreviewReady += OnPreviewReady;
        _pipeline.Log += OnLog;

        foreach (var monitor in _pipeline.AvailableMonitors)
        {
            Monitors.Add(monitor);
        }

        SelectedMonitor = Monitors.FirstOrDefault();

        if (Monitors.Count == 0)
        {
            AppendLog("Nenhum monitor disponível para captura.");
        }

        if (_pipeline.LogFilePath is not null)
        {
            AppendLog($"Log em arquivo: {_pipeline.LogFilePath}");
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || SelectedMonitor is null)
        {
            return;
        }

        var settings = new CaptureSettings
        {
            TargetFps = TargetFps,
            TargetBitrateKbps = BitrateKbps,
            RecordToFile = RecordToFile,
            RecordPath = CaptureSettings.DefaultRecordPath,
            ForceSoftwareEncoder = ForceSoftwareEncoder,
            TargetHeight = SelectedResolution.Height,
            RecordFormat = RecordFormat.Mp4,
            PreviewWidth = SelectedPreview.Width,
            PreviewFps = Math.Min(TargetFps, 30),
        };

        IsRunning = true;
        AppendLog($"Iniciando captura do monitor {SelectedMonitor.DeviceName}.");

        await _pipeline.StartAsync(SelectedMonitor.Index, settings, CancellationToken.None);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        await _pipeline.StopAsync();
        IsRunning = false;

        if (_pipeline.LastRecordingPath is { } recording)
        {
            AppendLog($"Gravação salva em {recording}");
        }

        Preview = null;
        _bitmap = null;
        StatsText = "Sem captura ativa";
        AppendLog("Captura parada.");
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var path = _pipeline.LogFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var argument = File.Exists(path)
            ? $"/select,\"{path}\""
            : $"\"{Path.GetDirectoryName(path)}\"";

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Não foi possível abrir a pasta do log: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenRecording()
    {
        var path = _pipeline.LastRecordingPath;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            AppendLog("Nenhuma gravação disponível ainda.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Não foi possível abrir a gravação: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ForceKeyframe()
    {
        _pipeline.RequestKeyframe();
        AppendLog("Keyframe solicitado.");
    }

    partial void OnSelectedResolutionChanged(ResolutionOption value) => ApplyRecommendedBitrate();

    partial void OnTargetFpsChanged(int value) => ApplyRecommendedBitrate();

    private void ApplyRecommendedBitrate()
    {
        var height = SelectedResolution.Height > 0
            ? SelectedResolution.Height
            : SelectedMonitor?.Resolution.Height ?? 1080;

        var baseline = height switch
        {
            <= 720 => 2500,
            <= 1080 => 5000,
            <= 1440 => 9000,
            _ => 16000,
        };

        BitrateKbps = (int)(baseline * Math.Max(1.0, TargetFps / 30.0));
    }

    partial void OnSelectedMonitorChanged(CaptureAdapterInfo? value)
    {
        if (value is null || !IsRunning)
        {
            return;
        }

        _ = SwitchMonitorAsync(value);
    }

    private async Task SwitchMonitorAsync(CaptureAdapterInfo monitor)
    {
        await StopAsync();
        SelectedMonitor = monitor;
        await StartAsync();
    }

    private void OnStatsUpdated(CaptureStats stats)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatsText = string.Format(
                CultureInfo.CurrentCulture,
                "Captura: {0:0} fps   Encode: {1:0} fps   Bitrate: {2:0} kbps   Descartados: {3}",
                stats.CapturedFps,
                stats.EncodedFps,
                stats.BitrateKbps,
                stats.DroppedFrames);

            EncoderText = $"Encoder: {stats.EncoderName} ({(stats.IsHardwareEncoder ? "hardware" : "software")})";
        });
    }

    private void OnPreviewReady(PreviewFrame frame)
    {
        if (Interlocked.Exchange(ref _previewPending, 1) == 1)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _previewPending, 0);

            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                Preview = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        });
    }

    private void OnLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        LogMessages.Add($"{DateTime.Now:HH:mm:ss}  {message}");

        while (LogMessages.Count > 200)
        {
            LogMessages.RemoveAt(0);
        }
    }
}
