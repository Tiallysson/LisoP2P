using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IIdentityStore _identity;
    private readonly IDiscoveryService _discovery;
    private readonly ManualPeerConnector _connector;
    private readonly NetworkOptions _options;

    public string Nickname => _identity.Nickname;
    public string ShortId => _identity.Id.Value.ToString("N")[..8];
    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    [ObservableProperty]
    private string _manualConnectAddress = "";

    [ObservableProperty]
    private string _statusText = "";

    public MainViewModel(IIdentityStore identity, IDiscoveryService discovery, ManualPeerConnector connector, NetworkOptions options)
    {
        _identity = identity;
        _discovery = discovery;
        _connector = connector;
        _options = options;

        _discovery.PeerAppeared += OnPeerAppeared;
        _discovery.PeerUpdated += OnPeerUpdated;
        _discovery.PeerLost += OnPeerLost;

        UpdateStatusText();
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
