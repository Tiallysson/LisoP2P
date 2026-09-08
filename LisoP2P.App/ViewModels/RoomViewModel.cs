using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
using LisoP2P.Core.Protocol;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;

namespace LisoP2P.App.ViewModels;

public sealed partial class RoomViewModel : ObservableObject, IDisposable
{
    private readonly IRoomService _rooms;
    private readonly IRoomChatRouter _router;
    private readonly IChatStore _chatStore;
    private readonly IIdentityStore _identity;
    private readonly IScreenShareSession _screenShare;
    private readonly IVoiceSession _voice;
    private readonly IAudioDeviceCatalog _audioDevices;
    private readonly ICapturePipeline _pipeline;

    private WriteableBitmap? _remoteBitmap;
    private int _remoteFramePending;
    private int _voiceStarting;

    public ObservableCollection<RoomMemberViewModel> Members { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public IReadOnlyList<CaptureAdapterInfo> Monitors => _pipeline.AvailableMonitors;
    public bool HasMultipleMonitors => Monitors.Count > 1;
    public IReadOnlyList<AudioDeviceInfo> InputDevices { get; }
    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; }

    public static IReadOnlyList<PushToTalkOption> PushToTalkOptions => ChatViewModel.PushToTalkOptions;

    [ObservableProperty]
    private string _roomName = "";

    [ObservableProperty]
    private string _draftText = "";

    [ObservableProperty]
    private string _memberCountText = "";

    [ObservableProperty]
    private CaptureAdapterInfo? _selectedMonitor;

    [ObservableProperty]
    private bool _canShare;

    [ObservableProperty]
    private bool _isSharing;

    [ObservableProperty]
    private bool _isWatching;

    [ObservableProperty]
    private string _shareBlockedReason = "";

    [ObservableProperty]
    private string _presenterText = "";

    [ObservableProperty]
    private ImageSource? _remoteVideo;

    [ObservableProperty]
    private bool _debugMode;

    [ObservableProperty]
    private string _videoStatsText = "";

    [ObservableProperty]
    private string _voiceStatsText = "";

    [ObservableProperty]
    private bool _isTransmittingVoice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedInputDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedOutputDevice;

    [ObservableProperty]
    private PushToTalkOption _pushToTalk = ChatViewModel.PushToTalkOptions[0];

    public RoomViewModel(
        IRoomService rooms,
        IRoomChatRouter router,
        IChatStore chatStore,
        IIdentityStore identity,
        IScreenShareSession screenShare,
        IVoiceSession voice,
        IAudioDeviceCatalog audioDevices,
        ICapturePipeline pipeline)
    {
        _rooms = rooms;
        _router = router;
        _chatStore = chatStore;
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

        _rooms.MembersChanged += OnMembersChanged;
        _router.MessageReceived += OnRoomMessageReceived;
        _screenShare.RemoteFrameReady += OnRemoteFrameReady;
        _screenShare.StateChanged += OnScreenShareStateChanged;
        _screenShare.StatsUpdated += OnScreenShareStatsUpdated;
        _voice.StateChanged += OnVoiceStateChanged;
        _voice.StatsUpdated += OnVoiceStatsUpdated;

        RefreshMembers();
    }

    public async Task LoadHistoryAsync()
    {
        if (_rooms.CurrentRoom is not { } room)
        {
            return;
        }

        var history = await _chatStore.GetRoomHistoryAsync(room).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() =>
        {
            Messages.Clear();

            foreach (var message in history)
            {
                Messages.Add(ChatMessageViewModel.From(message, NameOf(message.PeerId)));
            }
        });
    }

    private string NameOf(PeerId peer)
    {
        var member = _rooms.Members.FirstOrDefault(m => m.Id == peer);

        if (member is not null)
        {
            return member.IsSelf ? _identity.Nickname : member.Nickname;
        }

        return peer == _identity.Id ? _identity.Nickname : NicknameRules.FallbackFor(peer);
    }

    private void OnMembersChanged() => Application.Current.Dispatcher.Invoke(RefreshMembers);

    private void RefreshMembers()
    {
        var current = _rooms.Members;

        foreach (var member in current)
        {
            var existing = Members.FirstOrDefault(m => m.Id == member.Id);

            if (existing is null)
            {
                Members.Add(new RoomMemberViewModel(member));
            }
            else
            {
                existing.UpdateFrom(member);
            }
        }

        foreach (var stale in Members.Where(m => current.All(c => c.Id != m.Id)).ToList())
        {
            Members.Remove(stale);
        }

        RoomName = _rooms.RoomName;
        MemberCountText = Members.Count == 1 ? "1 membro" : $"{Members.Count} membros";

        RefreshShareState();
        _ = SyncMediaTargetsAsync();
    }

    /// <summary>
    /// Membership drives who receives media: a newcomer has to start getting frames and voice, and
    /// the bitrate ladder re-runs against the new receiver count.
    /// </summary>
    private async Task SyncMediaTargetsAsync()
    {
        var remote = _rooms.RemoteMemberIds;

        await _screenShare.UpdateTargetsAsync(remote).ConfigureAwait(false);

        if (_voice.IsActive)
        {
            await _voice.UpdateTargetsAsync(remote).ConfigureAwait(false);
        }
        else if (remote.Count > 0)
        {
            // Membership churns while the room converges, and every change lands here. Without this
            // guard a burst would open the playback device several times over.
            if (Interlocked.Exchange(ref _voiceStarting, 1) == 1)
            {
                return;
            }

            try
            {
                // Only playback opens here; the microphone is not touched until push-to-talk.
                await _voice.StartVoiceAsync(remote, BuildAudioSettings(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => VoiceStatsText = ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _voiceStarting, 0);
            }
        }
    }

    private AudioSettings BuildAudioSettings() => new()
    {
        InputDeviceId = SelectedInputDevice?.Id,
        OutputDeviceId = SelectedOutputDevice?.Id,
    };

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = DraftText.Trim();

        if (text.Length == 0 || text.Length > ChatMessagePayloadCodec.MaxTextLength)
        {
            return;
        }

        if (_rooms.CurrentRoom is not { } room)
        {
            return;
        }

        DraftText = "";

        var messageId = await _router.BroadcastAsync(room, text, CancellationToken.None).ConfigureAwait(false);

        var stored = new StoredMessage
        {
            MessageId = messageId,
            PeerId = _identity.Id,
            IsOutgoing = true,
            Text = text,
            SentAt = DateTimeOffset.UtcNow,
            // A room message has no single recipient to acknowledge it, so it is recorded as sent
            // rather than waiting on N acks for one id.
            Delivered = true,
            RoomId = room,
        };

        await _chatStore.SaveMessageAsync(stored).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() =>
            Messages.Add(ChatMessageViewModel.From(stored, _identity.Nickname)));
    }

    private void OnRoomMessageReceived(PeerId sender, ChatMessagePayload payload) =>
        _ = HandleRoomMessageAsync(sender, payload);

    private async Task HandleRoomMessageAsync(PeerId sender, ChatMessagePayload payload)
    {
        if (_rooms.CurrentRoom is not { } room)
        {
            return;
        }

        var stored = new StoredMessage
        {
            MessageId = payload.MessageId,
            PeerId = sender,
            IsOutgoing = false,
            Text = payload.Text,
            SentAt = DateTimeOffset.UtcNow,
            Delivered = true,
            RoomId = room,
        };

        await _chatStore.SaveMessageAsync(stored).ConfigureAwait(false);

        Application.Current.Dispatcher.Invoke(() =>
            Messages.Add(ChatMessageViewModel.From(stored, NameOf(sender))));
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
            await _screenShare.StartSharingAsync(
                _rooms.RemoteMemberIds,
                monitor.Index,
                new CaptureSettings(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => VideoStatsText = ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopShareAsync() => await _screenShare.StopSharingAsync();

    public void SetPushToTalk(bool pressed)
    {
        if (!_voice.IsActive || _voice.IsTransmitting == pressed)
        {
            return;
        }

        _voice.SetTransmitting(pressed);

        // The room indicator is driven by this explicit signal, never by watching packets arrive.
        _ = _rooms.SetSpeakingAsync(pressed);
    }

    private void OnVoiceStateChanged() => Application.Current.Dispatcher.Invoke(() =>
        IsTransmittingVoice = _voice.IsTransmitting);

    private void OnVoiceStatsUpdated(VoiceStats stats)
    {
        if (!DebugMode)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(() => VoiceStatsText = string.Format(
            CultureInfo.CurrentCulture,
            "Voz: fontes {0}   buffer {1}   Ocultados: {2}   Enviados: {3}   Recebidos: {4}",
            stats.ActiveSources,
            stats.JitterDepth,
            stats.ConcealedFrames,
            stats.PacketsSent,
            stats.PacketsReceived));
    }

    private void OnScreenShareStateChanged() => Application.Current.Dispatcher.Invoke(RefreshShareState);

    private void RefreshShareState()
    {
        IsSharing = _screenShare.IsSharing;
        IsWatching = _screenShare.IsWatching;

        var presenter = IsSharing ? _identity.Id : _screenShare.WatchingFrom;

        if (presenter is not null)
        {
            _rooms.SetSharingScreen(presenter, true);
            PresenterText = IsSharing ? "Você está compartilhando" : $"{NameOf(presenter)} está compartilhando";
        }
        else
        {
            _rooms.SetSharingScreen(_identity.Id, false);
            PresenterText = "";
        }

        // One presenter at a time is a product decision for this phase - the mesh does not carry N
        // simultaneous video streams well - not a limit of the protocol.
        var someoneElseSharing = IsWatching && !IsSharing;

        CanShare = !IsSharing
            && !someoneElseSharing
            && _rooms.Members.Any(m => !m.IsSelf && m.ConnectionState == SessionState.Connected);

        ShareBlockedReason = someoneElseSharing
            ? $"{NameOf(_screenShare.WatchingFrom!)} já está compartilhando a tela."
            : "";

        if (!IsWatching)
        {
            RemoteVideo = null;
            _remoteBitmap = null;
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
            "Degrau: {0} kbps @ {1} fps para {2} receptor(es)   Exibição: {3:0} fps   Perdidos: {4}   Decoder: {5}",
            stats.BitrateKbps,
            stats.Fps,
            stats.ReceiverCount,
            stats.DecodedFps,
            stats.DroppedFrames,
            stats.DecoderName));
    }

    private void OnRemoteFrameReady(PreviewFrame frame)
    {
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

    partial void OnSelectedInputDeviceChanged(AudioDeviceInfo? value) => ApplyAudioDevices();

    partial void OnSelectedOutputDeviceChanged(AudioDeviceInfo? value) => ApplyAudioDevices();

    private void ApplyAudioDevices() =>
        _voice.UpdateDevices(SelectedInputDevice?.Id, SelectedOutputDevice?.Id, AudioCaptureMode.Microphone);

    public void Dispose()
    {
        _rooms.MembersChanged -= OnMembersChanged;
        _router.MessageReceived -= OnRoomMessageReceived;
        _screenShare.RemoteFrameReady -= OnRemoteFrameReady;
        _screenShare.StateChanged -= OnScreenShareStateChanged;
        _screenShare.StatsUpdated -= OnScreenShareStatsUpdated;
        _voice.StateChanged -= OnVoiceStateChanged;
        _voice.StatsUpdated -= OnVoiceStatsUpdated;
    }
}
