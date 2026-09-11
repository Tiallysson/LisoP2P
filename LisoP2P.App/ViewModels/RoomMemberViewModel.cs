using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.App.Converters;
using LisoP2P.Core;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public sealed partial class RoomMemberViewModel : ObservableObject
{
    public PeerId Id { get; }
    public bool IsCurrentUser { get; }
    public string Fingerprint { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string _nickname;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isSpeaking;

    [ObservableProperty]
    private bool _isSharingScreen;

    [ObservableProperty]
    private SessionState _connectionState;

    /// <summary>
    /// False greys the member's microphone icon: a peer that is gone cannot speak, and the room
    /// must not keep showing a live indicator for it.
    /// </summary>
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private bool _showReconnecting;

    [ObservableProperty]
    private bool _showSharing;

    [ObservableProperty]
    private string _microphoneTooltip = "";

    public string Initials => NicknameToInitialsConverter.Initials(Nickname);

    public RoomMemberViewModel(RoomMember member)
    {
        Id = member.Id;
        IsCurrentUser = member.IsSelf;
        Fingerprint = PeerFingerprint.For(member.Id);
        _nickname = member.Nickname;
        _displayName = member.Nickname;
        UpdateFrom(member);
    }

    public void UpdateFrom(RoomMember member)
    {
        Nickname = member.Nickname;
        DisplayName = member.IsSelf ? $"{member.Nickname} (você)" : member.Nickname;
        ConnectionState = member.IsSelf ? SessionState.Connected : member.ConnectionState;
        IsConnected = ConnectionState == SessionState.Connected;
        IsSpeaking = member.IsSpeaking && IsConnected;
        IsSharingScreen = member.IsSharingScreen;
        MicrophoneTooltip = IsConnected ? "conectado" : "desconectado";

        ShowReconnecting = ConnectionState is SessionState.Reconnecting
            or SessionState.Connecting
            or SessionState.Handshaking;
        ShowSharing = IsSharingScreen && !ShowReconnecting;

        StateText = member.IsSelf
            ? ""
            : ConnectionState switch
            {
                SessionState.Connected => "conectado",
                SessionState.Reconnecting => "reconectando…",
                SessionState.Connecting or SessionState.Handshaking => "conectando…",
                _ => "desconectado",
            };
    }
}
