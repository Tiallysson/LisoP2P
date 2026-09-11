using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using LisoP2P.Net;

namespace LisoP2P.App.ViewModels;

public interface IConversationViewModel : INotifyPropertyChanged
{
    string Title { get; }
    string SubtitleText { get; }
    string PresenterText { get; }
    SessionState ConnectionState { get; }

    ObservableCollection<ChatMessageViewModel> Messages { get; }
    string DraftText { get; set; }
    IAsyncRelayCommand SendCommand { get; }

    bool IsWatching { get; }
    ImageSource? RemoteVideo { get; }
    string VideoOverlayText { get; }
    string VideoStatsText { get; }

    bool IsSharing { get; }
    bool CanShare { get; }
    string ShareBlockedReason { get; }
    IAsyncRelayCommand ShareScreenCommand { get; }
    IAsyncRelayCommand StopShareCommand { get; }

    bool DebugMode { get; set; }
    bool IsTransmittingVoice { get; }
    string VoiceStatsText { get; }

    void SetPushToTalk(bool pressed);
}
