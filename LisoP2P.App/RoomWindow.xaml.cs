using System.Windows;
using System.Windows.Input;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class RoomWindow : Window
{
    private readonly MainViewModel _viewModel;

    private bool _pushToTalkHeld;

    public RoomWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        PreviewKeyDown += OnPushToTalkKeyDown;
        PreviewKeyUp += OnPushToTalkKeyUp;

        // Losing focus with the key held would otherwise leave the microphone open and the room
        // indicator stuck on.
        Deactivated += (_, _) => ReleasePushToTalk();
    }

    private void OnPushToTalkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || _pushToTalkHeld || !MatchesPushToTalk(e))
        {
            return;
        }

        _pushToTalkHeld = true;
        _viewModel.ActiveRoom?.SetPushToTalk(true);
    }

    private void OnPushToTalkKeyUp(object sender, KeyEventArgs e)
    {
        if (MatchesPushToTalk(e))
        {
            ReleasePushToTalk();
        }
    }

    private void ReleasePushToTalk()
    {
        if (!_pushToTalkHeld)
        {
            return;
        }

        _pushToTalkHeld = false;
        _viewModel.ActiveRoom?.SetPushToTalk(false);
    }

    private bool MatchesPushToTalk(KeyEventArgs e)
    {
        if (_viewModel.ActiveRoom is not { } room)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var expected = room.PushToTalk.Key;

        return key == expected
            || (expected == Key.LeftCtrl && key == Key.RightCtrl)
            || (expected == Key.LeftAlt && key == Key.RightAlt)
            || (expected == Key.LeftShift && key == Key.RightShift);
    }

    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;

        if (_viewModel.ActiveRoom?.SendCommand.CanExecute(null) == true)
        {
            _viewModel.ActiveRoom.SendCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        PreviewKeyDown -= OnPushToTalkKeyDown;
        PreviewKeyUp -= OnPushToTalkKeyUp;
        ReleasePushToTalk();
        base.OnClosed(e);
    }
}
