using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.Core;

namespace LisoP2P.App.ViewModels;

public sealed partial class PeerViewModel : ObservableObject
{
    public PeerId Id { get; }

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private string _address;

    public PeerViewModel(DiscoveredPeer peer)
    {
        Id = peer.Id;
        _nickname = peer.Nickname;
        _address = peer.Address.ToString();
    }

    public void UpdateFrom(DiscoveredPeer peer)
    {
        Nickname = peer.Nickname;
        Address = peer.Address.ToString();
    }
}
