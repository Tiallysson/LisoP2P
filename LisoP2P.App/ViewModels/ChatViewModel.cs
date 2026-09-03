using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
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
    private readonly ICapturePipeline _pipeline;
    private readonly DispatcherTimer _headerTimer;
    private IPeerSession? _session;
    private WriteableBitmap? _remoteBitmap;
    private int _remoteFramePending;

    public PeerId PeerId => _peerId;
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public IReadOnlyList<CaptureAdapterInfo> Monitors => _pipeline.AvailableMonitors;
    public bool HasMultipleMonitors => Monitors.Count > 1;

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

    public ChatViewModel(
        PeerId peerId,
        string nickname,
        IChatStore chatStore,
        ISessionManager sessionManager,
        IIdentityStore identity,
        IScreenShareSession screenShare,
        ICapturePipeline pipeline)
    {
        _peerId = peerId;
        _peerNickname = nickname;
        _chatStore = chatStore;
        _sessionManager = sessionManager;
        _identity = identity;
        _screenShare = screenShare;
        _pipeline = pipeline;
        _selectedMonitor = _pipeline.AvailableMonitors.FirstOrDefault();

        _screenShare.RemoteFrameReady += OnRemoteFrameReady;
        _screenShare.StateChanged += OnScreenShareStateChanged;
        _screenShare.StatsUpdated += OnScreenShareStatsUpdated;

        _headerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _headerTimer.Tick += (_, _) => RefreshHeaderText();

        RefreshShareState();
    }

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
            await _screenShare.StartSharingAsync(_peerId, monitor.Index, new CaptureSettings(), CancellationToken.None);
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
        IsSharing = _screenShare.IsSharing && _screenShare.SharingWith == _peerId;
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
                SenderId = _identity.Id.Value,
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
            SenderId = _identity.Id.Value,
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
