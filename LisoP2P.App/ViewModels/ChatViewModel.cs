using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;

namespace LisoP2P.App.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly PeerId _peerId;
    private readonly IChatStore _chatStore;
    private readonly ISessionManager _sessionManager;
    private readonly IIdentityStore _identity;
    private readonly IScreenShareSession _screenShare;
    private readonly IVoiceSession _voice;
    private readonly IAudioDeviceCatalog _audioDevices;
    private readonly ICapturePipeline _pipeline;
    private readonly DispatcherTimer _headerTimer;
    private IPeerSession? _session;
    private WriteableBitmap? _remoteBitmap;
    private int _remoteFramePending;

    public PeerId PeerId => _peerId;
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public IReadOnlyList<CaptureAdapterInfo> Monitors => _pipeline.AvailableMonitors;
    public bool HasMultipleMonitors => Monitors.Count > 1;
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; }
    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }

    public static IReadOnlyList<AudioCaptureMode> CaptureModes { get; } =
    [
        AudioCaptureMode.Microphone,
        AudioCaptureMode.SystemLoopback,
        AudioCaptureMode.Both,
    ];

    public static IReadOnlyList<PushToTalkOption> PushToTalkOptions { get; } =
    [
        new PushToTalkOption("Ctrl", Key.LeftCtrl),
        new PushToTalkOption("Alt", Key.LeftAlt),
        new PushToTalkOption("Shift", Key.LeftShift),
        new PushToTalkOption("Espaço", Key.Space),
    ];

    [ObservableProperty]
    private string _peerNickname;

    [ObservableProperty]
    private string _draftText = "";

    [ObservableProperty]
    private string _headerText = "Desconectado";

    [ObservableProperty]
    private CaptureAdapterInfo? _selectedMonitor;

    [ObservableProperty]
    private bool _canShare;

    [ObservableProperty]
    private bool _isSharing;

    [ObservableProperty]
    private bool _isWatching;

    [ObservableProperty]
    private ImageSource? _remoteVideo;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private string _videoStatsText = "";

    [ObservableProperty]
    private bool _isVoiceActive;

    [ObservableProperty]
    private bool _isTransmittingVoice;

    [ObservableProperty]
    private bool _isPeerSpeaking;

    [ObservableProperty]
    private bool _openMicrophone;

    [ObservableProperty]
    private AudioCaptureMode _captureMode = AudioCaptureMode.Microphone;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedInputDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private PushToTalkOption _pushToTalk = PushToTalkOptions[0];

    [ObservableProperty]
    private string _voiceStatsText = "";

    public ChatViewModel(
        PeerId peerId,
        string nickname,
        IChatStore chatStore,
        ISessionManager sessionManager,
        IIdentityStore identity,
        IScreenShareSession screenShare,
        IVoiceSession voice,
        IAudioDeviceCatalog audioDevices,
        ICapturePipeline pipeline)
    {
        _peerId = peerId;
        _peerNickname = nickname;
        _chatStore = chatStore;
        _sessionManager = sessionManager;
        _identity = identity;
        _screenShare = screenShare;
        _voice = voice;
        _audioDevices = audioDevices;
        _pipeline = pipeline;
        _selectedMonitor = _pipeline.AvailableMonitors.FirstOrDefault();

        InputDevices = _audioDevices.GetInputDevices();
        OutputDevices = _audioDevices.GetOutputDevices();
        _selectedInputDevice = InputDevices.FirstOrDefault(device => device.IsDefault) ?? InputDevices.FirstOrDefault();
        _selectedOutputDevice = OutputDevices.FirstOrDefault(device => device.IsDefault) ?? OutputDevices.FirstOrDefault();

        _screenShare.RemoteFrameReady += OnRemoteFrameReady;
        _screenShare.StateChanged += OnScreenShareStateChanged;
        _screenShare.StatsUpdated += OnScreenShareStatsUpdated;
        _voice.StateChanged += OnVoiceStateChanged;
        _voice.StatsUpdated += OnVoiceStatsUpdated;

        _headerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _headerTimer.Tick += (_, _) => RefreshHeaderText();

        RefreshShareState();
        RefreshVoiceState();
    }

    [RelayCommand]
    private async Task ToggleVoiceAsync()
    {
        if (IsVoiceActive)
        {
            await _voice.StopVoiceAsync();
            return;
        }

        try
        {
            await _voice.StartVoiceAsync([_peerId], BuildAudioSettings(), CancellationToken.None);

            if (OpenMicrophone)
            {
                _voice.SetTransmitting(true);
            }
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => VoiceStatsText = ex.Message);
        }
    }

    public void SetPushToTalk(bool pressed)
    {
        if (!IsVoiceActive || OpenMicrophone)
        {
            return;
        }

        _voice.SetTransmitting(pressed);
    }

    private AudioSettings BuildAudioSettings() => new()
    {
        Mode = CaptureMode,
        InputDeviceId = SelectedInputDevice?.Id,
        OutputDeviceId = SelectedOutputDevice?.Id,
    };

    private void OnVoiceStateChanged() => Application.Current.Dispatcher.Invoke(RefreshVoiceState);

    private void RefreshVoiceState()
    {
        IsVoiceActive = _voice.IsActive && _voice.Targets.Contains(_peerId);
        IsTransmittingVoice = _voice.IsTransmitting;
        IsPeerSpeaking = _voice.IsPeerSpeaking;

        if (!IsVoiceActive && !_voice.IsReceiving)
        {
            VoiceStatsText = "";
        }
    }

    private void OnVoiceStatsUpdated(VoiceStats stats)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsPeerSpeaking = _voice.IsPeerSpeaking;

            if (!DebugMode)
            {
                return;
            }

            VoiceStatsText = string.Format(
                CultureInfo.CurrentCulture,
                "Voz: buffer {0}   Ocultados: {1}   Atrasados: {2}   Excedente: {3}   Enviados: {4}   Recebidos: {5}",
                stats.JitterDepth,
                stats.ConcealedFrames,
                stats.LateDiscards,
                stats.OverflowDiscards,
                stats.PacketsSent,
                stats.PacketsReceived);
        });
    }

    partial void OnOpenMicrophoneChanged(bool value)
    {
        if (!IsVoiceActive)
        {
            return;
        }

        _voice.SetTransmitting(value);
    }

    partial void OnCaptureModeChanged(AudioCaptureMode value) => ApplyAudioDevices();

    partial void OnSelectedInputDeviceChanged(AudioDeviceInfo? value) => ApplyAudioDevices();

    partial void OnSelectedOutputDeviceChanged(AudioDeviceInfo? value) => ApplyAudioDevices();

    private void ApplyAudioDevices() =>
        _voice.UpdateDevices(SelectedInputDevice?.Id, SelectedOutputDevice?.Id, CaptureMode);

    [RelayCommand]
    private async Task ShareScreenAsync()
    {
        var monitor = SelectedMonitor ?? Monitors.FirstOrDefault();

        if (monitor is null)
        {
            return;
        }

        try
        {
            await _screenShare.StartSharingAsync([_peerId], monitor.Index, new CaptureSettings(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => VideoStatsText = ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopShareAsync() => await _screenShare.StopSharingAsync();

    private void OnScreenShareStateChanged() =>
        Application.Current.Dispatcher.Invoke(RefreshShareState);

    private void RefreshShareState()
    {
        IsSharing = _screenShare.IsSharing && _screenShare.SharingWith.Contains(_peerId);
        IsWatching = _screenShare.IsWatching && _screenShare.WatchingFrom == _peerId;
        CanShare = !IsSharing && _session?.State == SessionState.Connected;

        if (!IsWatching)
        {
            RemoteVideo = null;
            _remoteBitmap = null;
            VideoStatsText = "";
        }
    }

    private void OnScreenShareStatsUpdated(ScreenShareStats stats)
    {
        if (!DebugMode)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(() => VideoStatsText = string.Format(
            CultureInfo.CurrentCulture,
            "Exibição: {0:0} fps   Perdidos: {1}   Em remontagem: {2}   Keyframes pedidos: {3}   Decoder: {4}",
            stats.DecodedFps,
            stats.DroppedFrames,
            stats.PendingFrames,
            stats.KeyframeRequests,
            stats.DecoderName));
    }

    private void OnRemoteFrameReady(PreviewFrame frame)
    {
        if (_screenShare.WatchingFrom != _peerId)
        {
            return;
        }

        if (Interlocked.Exchange(ref _remoteFramePending, 1) == 1)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _remoteFramePending, 0);

            if (_remoteBitmap is null || _remoteBitmap.PixelWidth != frame.Width || _remoteBitmap.PixelHeight != frame.Height)
            {
                _remoteBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                RemoteVideo = _remoteBitmap;
            }

            _remoteBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        });
    }

    partial void OnDebugModeChanged(bool value)
    {
        if (!value)
        {
            VideoStatsText = "";
            VoiceStatsText = "";
        }
    }

    public async Task LoadHistoryAsync()
    {
        var history = await _chatStore.GetHistoryAsync(_peerId).ConfigureAwait(false);
        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Clear();
            foreach (var message in history)
            {
                Messages.Add(ChatMessageViewModel.From(message));
            }
        });
    }

    public async Task EnsureConnectedAsync(DiscoveredPeer peer)
    {
        if (_sessionManager.Sessions.TryGetValue(_peerId, out var existing))
        {
            AttachSession(existing);
            return;
        }

        try
        {
            var session = await _sessionManager.ConnectAsync(peer, CancellationToken.None).ConfigureAwait(false);
            AttachSession(session);
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => HeaderText = "Desconectado");
        }
    }

    private void AttachSession(IPeerSession session)
    {
        _session = session;
        session.StateChanged += OnSessionStateChanged;
        session.MessageReceived += OnMessageReceived;
        session.RemoteNicknameChanged += OnRemoteNicknameChanged;

        UpdateNickname(session.RemoteNickname);

        Application.Current.Dispatcher.Invoke(RefreshHeaderText);
        _headerTimer.Start();

        if (session.State == SessionState.Connected)
        {
            _ = ResendUndeliveredAsync();
        }
    }

    public void Detach()
    {
        _headerTimer.Stop();

        _screenShare.RemoteFrameReady -= OnRemoteFrameReady;
        _screenShare.StateChanged -= OnScreenShareStateChanged;
        _screenShare.StatsUpdated -= OnScreenShareStatsUpdated;
        _voice.StateChanged -= OnVoiceStateChanged;
        _voice.StatsUpdated -= OnVoiceStatsUpdated;

        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            _session.MessageReceived -= OnMessageReceived;
            _session.RemoteNicknameChanged -= OnRemoteNicknameChanged;
            _session = null;
        }
    }

    public void Dispose() => Detach();

    public void UpdateNickname(string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname) || string.Equals(nickname, PeerNickname, StringComparison.Ordinal))
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() => PeerNickname = nickname);
    }

    private void OnRemoteNicknameChanged(string nickname) => UpdateNickname(nickname);

    private void OnSessionStateChanged(SessionState state)
    {
        Application.Current.Dispatcher.Invoke(RefreshHeaderText);

        if (state == SessionState.Connected)
        {
            _ = ResendUndeliveredAsync();
        }
    }

    private void RefreshHeaderText()
    {
        RefreshShareState();
        RefreshVoiceState();

        var session = _session;
        HeaderText = session?.State switch
        {
            SessionState.Connected => session.RoundTripTime is { } rtt
                ? $"Conectado · {rtt.TotalMilliseconds:0} ms"
                : "Conectado",
            SessionState.Reconnecting => "Reconectando…",
            SessionState.Connecting or SessionState.Handshaking => "Conectando…",
            _ => "Desconectado",
        };
    }

    private void OnMessageReceived(Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.ChatMessage:
                _ = HandleIncomingChatMessageAsync(envelope);
                break;
            case MessageType.ChatAck:
                _ = HandleChatAckAsync(envelope);
                break;
        }
    }

    private async Task HandleIncomingChatMessageAsync(Envelope envelope)
    {
        if (!ChatMessagePayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        var stored = new StoredMessage
        {
            MessageId = payload.MessageId,
            PeerId = _peerId,
            IsOutgoing = false,
            Text = payload.Text,
            SentAt = DateTimeOffset.UtcNow,
            Delivered = true,
        };
        await _chatStore.SaveMessageAsync(stored).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() => Messages.Add(ChatMessageViewModel.From(stored)));

        var session = _session;
        if (session is not null)
        {
            var ack = new Envelope
            {
                Version = ProtocolCodec.CurrentVersion,
                Type = MessageType.ChatAck,
                SenderId = _identity.Id.PublicKeyBytes,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = ChatAckPayloadCodec.Encode(new ChatAckPayload { MessageId = payload.MessageId }),
            };
            await session.SendAsync(ack, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task HandleChatAckAsync(Envelope envelope)
    {
        if (!ChatAckPayloadCodec.TryDecode(envelope.Payload, out var payload) || payload is null)
        {
            return;
        }

        await _chatStore.MarkDeliveredAsync(payload.MessageId).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() =>
        {
            var message = Messages.FirstOrDefault(m => m.MessageId == payload.MessageId);
            message?.MarkDelivered();
        });
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = DraftText.Trim();
        if (text.Length == 0 || text.Length > ChatMessagePayloadCodec.MaxTextLength)
        {
            return;
        }

        DraftText = "";

        var messageId = Guid.NewGuid();
        var stored = new StoredMessage
        {
            MessageId = messageId,
            PeerId = _peerId,
            IsOutgoing = true,
            Text = text,
            SentAt = DateTimeOffset.UtcNow,
            Delivered = false,
        };
        await _chatStore.SaveMessageAsync(stored).ConfigureAwait(false);
        Messages.Add(ChatMessageViewModel.From(stored));

        await SendChatEnvelopeAsync(messageId, text).ConfigureAwait(false);
    }

    private async Task SendChatEnvelopeAsync(Guid messageId, string text)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var envelope = new Envelope
        {
            Version = ProtocolCodec.CurrentVersion,
            Type = MessageType.ChatMessage,
            SenderId = _identity.Id.PublicKeyBytes,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = ChatMessagePayloadCodec.Encode(new ChatMessagePayload { MessageId = messageId, Text = text }),
        };
        await session.SendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ResendUndeliveredAsync()
    {
        var pending = await _chatStore.GetUndeliveredAsync(_peerId).ConfigureAwait(false);
        foreach (var message in pending)
        {
            await SendChatEnvelopeAsync(message.MessageId, message.Text).ConfigureAwait(false);
        }
    }
}
