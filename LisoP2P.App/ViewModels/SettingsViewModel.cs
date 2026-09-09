using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.App.Services;
using LisoP2P.Core;
using LisoP2P.Core.Settings;
using LisoP2P.Media;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public sealed record BitrateStep(string Receivers, string Quality);

/// <summary>
/// Every user-facing preference in one place, applied only on "Salvar". Nothing here takes effect
/// field by field: a half-typed port would otherwise rebuild the network stack on every keystroke.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;
    private readonly IIdentityStore _identity;
    private readonly IAppShell _shell;
    private readonly IErrorPresenter _errors;

    public ObservableCollection<CaptureAdapterInfo> Monitors { get; } = [];
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; }
    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }
    public IReadOnlyList<AudioCaptureMode> CaptureModes { get; } =
        [AudioCaptureMode.Microphone, AudioCaptureMode.SystemLoopback, AudioCaptureMode.Both];

    public IReadOnlyList<CaptureResolutionOption> Resolutions { get; } =
    [
        new CaptureResolutionOption("Nativa", AppSettings.NativeResolution),
        new CaptureResolutionOption("1080p", "1920x1080"),
        new CaptureResolutionOption("720p", "1280x720"),
    ];

    public IReadOnlyList<int> FpsOptions { get; } = [15, 24, 30, 45, 60];

    /// <summary>
    /// The fase 5 ladder, read-only here: quality is chosen by the receiver count at share time,
    /// and letting the user pin it would defeat the point of the ladder.
    /// </summary>
    public IReadOnlyList<BitrateStep> BitrateLadderSteps { get; } =
    [
        Step("1 receptor", 1),
        Step("2 receptores", 2),
        Step("3 receptores", 3),
        Step("4 ou mais", 4),
    ];

    public string Fingerprint => _identity.Fingerprint;
    public string PeerIdText => _identity.Id.ToHex();
    public string SettingsFilePath => _store.FilePath;

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private CaptureAdapterInfo? _selectedMonitor;

    [ObservableProperty]
    private CaptureResolutionOption _selectedResolution;

    [ObservableProperty]
    private int _captureFps;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedInputDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private AudioCaptureMode _audioMode;

    [ObservableProperty]
    private bool _pushToTalkEnabled;

    [ObservableProperty]
    private Key _pushToTalkKey;

    [ObservableProperty]
    private bool _isCapturingKey;

    [ObservableProperty]
    private int _discoveryPort;

    [ObservableProperty]
    private int _sessionPort;

    [ObservableProperty]
    private int _mediaPort;

    [ObservableProperty]
    private string _networkWarning = "";

    [ObservableProperty]
    private string _validationError = "";

    [ObservableProperty]
    private string _fingerprintCopiedText = "";

    [ObservableProperty]
    private bool _isApplying;

    public string PushToTalkKeyText => PushToTalkKeys.Describe(PushToTalkKey);

    public bool PortsChanged
    {
        get
        {
            var stored = _store.Current;
            return stored.DiscoveryPort != DiscoveryPort
                || stored.SessionPort != SessionPort
                || stored.MediaPort != MediaPort;
        }
    }

    /// <summary>Raised when the dialog should close itself after a successful save.</summary>
    public event Action? Saved;

    public SettingsViewModel(
        IAppSettingsStore store,
        IIdentityStore identity,
        IAudioDeviceCatalog audioDevices,
        IScreenCapture screenCapture,
        IAppShell shell,
        IErrorPresenter errors)
    {
        _store = store;
        _identity = identity;
        _shell = shell;
        _errors = errors;

        InputDevices = SafeDevices(audioDevices.GetInputDevices);
        OutputDevices = SafeDevices(audioDevices.GetOutputDevices);

        foreach (var monitor in SafeMonitors(screenCapture))
        {
            Monitors.Add(monitor);
        }

        var settings = store.Current;

        _nickname = identity.Nickname;
        _selectedMonitor = Monitors.FirstOrDefault(m => m.Index == settings.PreferredMonitorIndex)
            ?? Monitors.FirstOrDefault();
        _selectedResolution = Resolutions.FirstOrDefault(r =>
            string.Equals(r.Value, settings.CaptureResolution, StringComparison.OrdinalIgnoreCase))
            ?? Resolutions[0];
        _captureFps = FpsOptions.Contains(settings.CaptureFps) ? settings.CaptureFps : 30;
        _selectedInputDevice = FindDevice(InputDevices, settings.AudioInputDeviceId);
        _selectedOutputDevice = FindDevice(OutputDevices, settings.AudioOutputDeviceId);
        _audioMode = settings.AudioMode;
        _pushToTalkEnabled = settings.PushToTalkEnabled;
        _pushToTalkKey = PushToTalkKeys.Parse(settings.PushToTalkKey);
        _discoveryPort = settings.DiscoveryPort;
        _sessionPort = settings.SessionPort;
        _mediaPort = settings.MediaPort;
    }

    private static BitrateStep Step(string label, int receivers)
    {
        var (bitrate, fps) = BitrateLadder.SelectFor(receivers);
        return new BitrateStep(label, $"{bitrate} kbps @ {fps} fps");
    }

    private static AudioDeviceInfo? FindDevice(IReadOnlyList<AudioDeviceInfo> devices, string? id) =>
        devices.FirstOrDefault(device => device.Id == id)
        ?? devices.FirstOrDefault(device => device.IsDefault)
        ?? devices.FirstOrDefault();

    private IReadOnlyList<AudioDeviceInfo> SafeDevices(Func<IReadOnlyList<AudioDeviceInfo>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            // A broken audio driver must not keep the user out of the network tab.
            _errors.ShowTransient($"Não foi possível listar dispositivos de áudio: {ex.Message}", ErrorSeverity.Warning);
            return [];
        }
    }

    private IReadOnlyList<CaptureAdapterInfo> SafeMonitors(IScreenCapture capture)
    {
        try
        {
            return capture.AvailableMonitors;
        }
        catch (Exception ex)
        {
            _errors.ShowTransient($"Não foi possível listar monitores: {ex.Message}", ErrorSeverity.Warning);
            return [];
        }
    }

    partial void OnPushToTalkKeyChanged(Key value) => OnPropertyChanged(nameof(PushToTalkKeyText));

    [RelayCommand]
    private void BeginKeyCapture()
    {
        IsCapturingKey = true;
        ValidationError = "";
    }

    /// <summary>Records the next key the window sees while capture is armed.</summary>
    public void CaptureKey(Key key)
    {
        if (!IsCapturingKey || key == Key.None)
        {
            return;
        }

        IsCapturingKey = false;

        if (key != Key.Escape)
        {
            PushToTalkKey = key;
        }
    }

    [RelayCommand]
    private void CopyFingerprint()
    {
        try
        {
            Clipboard.SetText(Fingerprint);
            FingerprintCopiedText = "Fingerprint copiado.";
        }
        catch (Exception ex)
        {
            FingerprintCopiedText = $"Não foi possível copiar: {ex.Message}";
        }
    }

    /// <summary>
    /// Binds and releases each port to answer "is it free right now?". It is a snapshot, not a
    /// reservation — the startup path still has to survive losing the race.
    /// </summary>
    [RelayCommand]
    private void CheckPorts()
    {
        if (!ValidatePorts())
        {
            return;
        }

        var busy = PortProbe.CheckAll(DiscoveryPort, SessionPort, MediaPort)
            .Where(check => !check.IsAvailable)
            .ToList();

        NetworkWarning = busy.Count == 0
            ? "Portas livres."
            : "Em uso agora: " + string.Join(", ", busy.Select(c => $"{c.Port} ({c.Role})"));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsApplying || !Validate())
        {
            return;
        }

        var portsChanged = PortsChanged;

        if (portsChanged)
        {
            var busy = PortProbe.CheckAll(DiscoveryPort, SessionPort, MediaPort)
                .Where(check => !check.IsAvailable)
                .ToList();

            if (busy.Count > 0)
            {
                NetworkWarning = "Em uso agora: " + string.Join(", ", busy.Select(c => $"{c.Port} ({c.Role})"));
                ValidationError = "Escolha portas livres antes de aplicar.";
                return;
            }
        }

        IsApplying = true;

        try
        {
            if (!string.Equals(Nickname, _identity.Nickname, StringComparison.Ordinal))
            {
                _identity.SetNickname(Nickname);
            }

            var settings = _store.Current;
            settings.Nickname = _identity.Nickname;
            settings.PreferredMonitorIndex = SelectedMonitor?.Index ?? 0;
            settings.CaptureResolution = SelectedResolution.Value;
            settings.CaptureFps = CaptureFps;
            settings.AudioInputDeviceId = SelectedInputDevice?.Id;
            settings.AudioOutputDeviceId = SelectedOutputDevice?.Id;
            settings.AudioMode = AudioMode;
            settings.PushToTalkEnabled = PushToTalkEnabled;
            settings.PushToTalkKey = PushToTalkKey.ToString();
            settings.DiscoveryPort = DiscoveryPort;
            settings.SessionPort = SessionPort;
            settings.MediaPort = MediaPort;

            _store.Save(settings);

            // Audio applies live: the acceptance test switches the output device mid-call.
            _shell.ApplyAudioDevices(settings.AudioInputDeviceId, settings.AudioOutputDeviceId, settings.AudioMode);

            if (portsChanged && !await _shell.RestartNetworkAsync().ConfigureAwait(true))
            {
                return;
            }

            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            ValidationError = ex.Message;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private bool Validate()
    {
        ValidationError = "";

        if (!NicknameRules.IsValid(Nickname))
        {
            ValidationError = $"Informe um nome com até {NicknameRules.MaxLength} caracteres.";
            return false;
        }

        return ValidatePorts();
    }

    private bool ValidatePorts()
    {
        ValidationError = "";

        foreach (var (port, role) in new[]
                 {
                     (DiscoveryPort, "descoberta"),
                     (SessionPort, "sessão"),
                     (MediaPort, "mídia"),
                 })
        {
            if (!NetworkPorts.IsValid(port))
            {
                ValidationError =
                    $"A porta de {role} deve estar entre {NetworkPorts.MinPort} e {NetworkPorts.MaxPort}.";
                return false;
            }
        }

        if (DiscoveryPort == SessionPort || DiscoveryPort == MediaPort || SessionPort == MediaPort)
        {
            ValidationError = "As três portas precisam ser diferentes entre si.";
            return false;
        }

        return true;
    }
}
