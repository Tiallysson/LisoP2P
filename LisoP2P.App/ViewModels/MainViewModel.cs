using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
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
    private readonly NetworkOptions _options;

    public string Nickname => _identity.Nickname;
    public string ShortId => _identity.Id.Value.ToString("N")[..8];
    public ObservableCollection<PeerViewModel> Peers { get; } = [];

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
        NetworkOptions options)
    {
        _identity = identity;
        _discovery = discovery;
        _connector = connector;
        _sessionManager = sessionManager;
        _chatStore = chatStore;
        _options = options;

        _discovery.PeerAppeared += OnPeerAppeared;
        _discovery.PeerUpdated += OnPeerUpdated;
        _discovery.PeerLost += OnPeerLost;

        UpdateStatusText();
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

        var chat = new ChatViewModel(peerVm.Id, peerVm.Nickname, _chatStore, _sessionManager, _identity);
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
        StatusText = $"Discovery: {_options.DiscoveryPort}  Session: {_options.SessionPort}  Peers: {Peers.Count}";
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
