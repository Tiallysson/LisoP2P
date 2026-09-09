using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public sealed partial class RoomMemberViewModel : ObservableObject
{
    public PeerId Id { get; }
    public bool IsSelf { get; }
    public string Fingerprint { get; }

    [ObservableProperty]
    private string _nickname;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isSharingScreen;

    /// <summary>
    /// False greys the member's microphone icon: a peer that is gone cannot speak, and the room
    /// must not keep showing a live indicator for it.
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private string _microphoneTooltip = "";

    public RoomMemberViewModel(RoomMember member)
    {
        Id = member.Id;
        IsSelf = member.IsSelf;
        Fingerprint = PeerFingerprint.For(member.Id);
        _nickname = member.Nickname;
        UpdateFrom(member);
    }

    public void UpdateFrom(RoomMember member)
    {
        Nickname = member.IsSelf ? $"{member.Nickname} (você)" : member.Nickname;
        IsConnected = member.IsSelf || member.ConnectionState == SessionState.Connected;
        IsSpeaking = member.IsSpeaking && IsConnected;
        IsSharingScreen = member.IsSharingScreen;
        MicrophoneTooltip = IsConnected ? "conectado" : "desconectado";
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
