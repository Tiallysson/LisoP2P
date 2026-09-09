using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
using LisoP2P.Media;
using LisoP2P.Net;
using LisoP2P.Storage;

namespace LisoP2P.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ManualPeerConnector _connector;
    private readonly ISessionManager _sessionManager;
    private readonly IChatStore _chatStore;
    private readonly IScreenShareSession _screenShare;
    private readonly IVoiceSession _voice;
    private readonly IRoomService _rooms;
    private readonly IRoomChatRouter _router;
    private readonly IAudioDeviceCatalog _audioDevices;
    private readonly ICapturePipeline _pipeline;
    private readonly NetworkOptions _options;

    public string ShortId => _identity.Id.ToString();
    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private string _nicknameError = "";

    [ObservableProperty]
    private string _manualConnectAddress = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private PeerViewModel? _selectedPeer;

    [ObservableProperty]
    private ChatViewModel? _activeChat;

    [ObservableProperty]
    private RoomViewModel? _activeRoom;

    [ObservableProperty]
    private string _newRoomName = "Sala";

    [ObservableProperty]
    private string _roomStatusText = "Nenhuma sala ativa";

    public MainViewModel(
        IIdentityStore identity,
        IDiscoveryService discovery,
        ManualPeerConnector connector,
        ISessionManager sessionManager,
        IChatStore chatStore,
        IScreenShareSession screenShare,
        IVoiceSession voice,
        IRoomService rooms,
        IRoomChatRouter router,
        IAudioDeviceCatalog audioDevices,
        ICapturePipeline pipeline,
        NetworkOptions options)
    {
        _identity = identity;
        _discovery = discovery;
        _connector = connector;
        _sessionManager = sessionManager;
        _chatStore = chatStore;
        _screenShare = screenShare;
        _voice = voice;
        _rooms = rooms;
        _router = router;
        _audioDevices = audioDevices;
        _pipeline = pipeline;
        _options = options;
        _nickname = identity.Nickname;

        _identity.NicknameChanged += OnIdentityNicknameChanged;
        _discovery.PeerAppeared += OnPeerAppeared;
        _discovery.PeerUpdated += OnPeerUpdated;
        _discovery.PeerLost += OnPeerLost;
        _rooms.MembersChanged += OnRoomMembersChanged;

        UpdateStatusText();
    }

    private void OnIdentityNicknameChanged(string nickname)
    {
        Application.Current.Dispatcher.Invoke(() => Nickname = nickname);
    }

    [RelayCommand]
    private void SaveNickname()
    {
        if (!NicknameRules.IsValid(Nickname))
        {
            NicknameError = $"Informe um nome com até {NicknameRules.MaxLength} caracteres.";
            Nickname = _identity.Nickname;
            return;
        }

        _identity.SetNickname(Nickname);
        Nickname = _identity.Nickname;
        NicknameError = "";
    }

    partial void OnSelectedPeerChanged(PeerViewModel? value)
    {
        if (value is not null)
        {
            _ = OpenConversationAsync(value);
        }
    }

    private async Task OpenConversationAsync(PeerViewModel peerVm)
    {
        ActiveChat?.Dispose();

        var chat = new ChatViewModel(
            peerVm.Id,
            peerVm.Nickname,
            _chatStore,
            _sessionManager,
            _identity,
            _screenShare,
            _voice,
            _audioDevices,
            _pipeline);
        ActiveChat = chat;

        await chat.LoadHistoryAsync().ConfigureAwait(false);

        var peer = _discovery.Peers.FirstOrDefault(p => p.Id == peerVm.Id);
        if (peer is not null)
        {
            await chat.EnsureConnectedAsync(peer).ConfigureAwait(false);
        }
    }

    private void OnPeerAppeared(DiscoveredPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Peers.Add(new PeerViewModel(peer));
            UpdateStatusText();
        });
    }

    private void OnPeerUpdated(DiscoveredPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.Id == peer.Id);
            existing?.UpdateFrom(peer);

            if (ActiveChat?.PeerId == peer.Id)
            {
                ActiveChat.UpdateNickname(peer.Nickname);
            }
        });
    }

    private void OnPeerLost(PeerId id)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.Id == id);
            if (existing is not null)
            {
                Peers.Remove(existing);
            }

            UpdateStatusText();
        });
    }

    private void UpdateStatusText()
    {
        StatusText =
            $"Discovery: {_options.DiscoveryPort}  Session: {_options.SessionPort}  " +
            $"Mídia: {_options.MediaPort}  Peers: {Peers.Count}";
    }

    private void OnRoomMembersChanged() => Application.Current.Dispatcher.Invoke(SyncRoomState);

    private void SyncRoomState()
    {
        if (_rooms.IsInRoom)
        {
            if (ActiveRoom is null)
            {
                var room = new RoomViewModel(
                    _rooms,
                    _router,
                    _chatStore,
                    _identity,
                    _screenShare,
                    _voice,
                    _audioDevices,
                    _pipeline);

                ActiveRoom = room;
                _ = room.LoadHistoryAsync();
            }

            RoomStatusText = $"{_rooms.RoomName} · {_rooms.Members.Count} membro(s)";
            return;
        }

        ActiveRoom?.Dispose();
        ActiveRoom = null;
        RoomStatusText = "Nenhuma sala ativa";
    }

    [RelayCommand]
    private void CreateRoom()
    {
        _rooms.CreateRoom(NewRoomName);
        SyncRoomState();
    }

    [RelayCommand]
    private async Task InviteSelectedPeerAsync()
    {
        if (SelectedPeer is null)
        {
            return;
        }

        try
        {
            await _rooms.InviteAsync(SelectedPeer.Id, CancellationToken.None);
            SyncRoomState();
        }
        catch (Exception ex)
        {
            RoomStatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        await _rooms.LeaveAsync();
        SyncRoomState();
    }

    [RelayCommand]
    private void ConnectManually()
    {
        if (IPAddress.TryParse(ManualConnectAddress, out var address))
        {
            _connector.Connect(address);
        }
    }
}
