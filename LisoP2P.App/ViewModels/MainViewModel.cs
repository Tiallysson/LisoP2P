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
    private readonly IAudioDeviceCatalog _audioDevices;
    private readonly ICapturePipeline _pipeline;
    private readonly NetworkOptions _options;

    public string ShortId => _identity.Id.Value.ToString("N")[..8];
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

    public MainViewModel(
        IIdentityStore identity,
        IDiscoveryService discovery,
        ManualPeerConnector connector,
        ISessionManager sessionManager,
        IChatStore chatStore,
        IScreenShareSession screenShare,
        IVoiceSession voice,
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
        _audioDevices = audioDevices;
        _pipeline = pipeline;
        _options = options;
        _nickname = identity.Nickname;

        _identity.NicknameChanged += OnIdentityNicknameChanged;
        _discovery.PeerAppeared += OnPeerAppeared;
        _discovery.PeerUpdated += OnPeerUpdated;
        _discovery.PeerLost += OnPeerLost;

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
            var existing = Peers.FirstOrDefault(p => p.Id.Value == peer.Id.Value);
            existing?.UpdateFrom(peer);

            if (ActiveChat?.PeerId.Value == peer.Id.Value)
            {
                ActiveChat.UpdateNickname(peer.Nickname);
            }
        });
    }

    private void OnPeerLost(PeerId id)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Peers.FirstOrDefault(p => p.Id.Value == id.Value);
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

    [RelayCommand]
    private void ConnectManually()
    {
        if (IPAddress.TryParse(ManualConnectAddress, out var address))
        {
            _connector.Connect(address);
        }
    }
}
