using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public sealed partial class RoomMemberViewModel : ObservableObject
{
    public PeerId Id { get; }
    public bool IsSelf { get; }

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isSharingScreen;

    [ObservableProperty]
    private string _stateText = "";

    public RoomMemberViewModel(RoomMember member)
    {
        Id = member.Id;
        IsSelf = member.IsSelf;
        _nickname = member.Nickname;
        UpdateFrom(member);
    }

    public void UpdateFrom(RoomMember member)
    {
        Nickname = member.IsSelf ? $"{member.Nickname} (você)" : member.Nickname;
        IsSpeaking = member.IsSpeaking;
        IsSharingScreen = member.IsSharingScreen;
        StateText = member.IsSelf
            ? ""
            : member.ConnectionState switch
            {
                SessionState.Connected => "conectado",
                SessionState.Reconnecting => "reconectando…",
                SessionState.Connecting or SessionState.Handshaking => "conectando…",
                _ => "desconectado",
            };
    }
}
