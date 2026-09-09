using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LisoP2P.App.Services;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class RoomWindow : Window
{
    private readonly MainViewModel _viewModel;

    private bool _pushToTalkHeld;

    public RoomWindow(IAppShell shell)
    {
        InitializeComponent();

        _viewModel = shell.MainViewModel;
        DataContext = _viewModel;
        Notifications.ItemsSource = shell.Notifications.Notifications;

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
        if (_viewModel.ActiveRoom is null)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var expected = _viewModel.PushToTalkKey;

        if (expected == Key.Space && Keyboard.FocusedElement is TextBoxBase)
        {
            return false;
        }

        return PushToTalkKeys.Matches(expected, key);
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
