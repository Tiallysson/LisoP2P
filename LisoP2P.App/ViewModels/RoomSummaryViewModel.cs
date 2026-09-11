using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.App.Converters;
using LisoP2P.Core;

namespace LisoP2P.App.ViewModels;

public sealed partial class RoomSummaryViewModel : ObservableObject
{
    public RoomId Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string _name;

    [ObservableProperty]
    private bool _hasUnread;

    [ObservableProperty]
    private int _memberCount;

    public string Initials => NicknameToInitialsConverter.Initials(Name);

    public RoomSummaryViewModel(RoomId id, string name)
    {
        Id = id;
        _name = name;
    }
}
