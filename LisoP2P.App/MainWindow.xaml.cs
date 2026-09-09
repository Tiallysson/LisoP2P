using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LisoP2P.App.Services;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class MainWindow : Window
{
    private readonly IAppShell _shell;
    private MainViewModel _viewModel;
    private ChatViewModel? _boundChat;
    private CaptureTestWindow? _captureWindow;
    private RoomWindow? _roomWindow;
    private SettingsWindow? _settingsWindow;
    private bool _userScrolledUp;
    private bool _pushToTalkHeld;

    public MainWindow(IAppShell shell)
    {
        InitializeComponent();

        _shell = shell;
        _viewModel = shell.MainViewModel;

        Bind();
        Notifications.ItemsSource = shell.Notifications.Notifications;

        // A port change replaces the whole network stack, and the view model with it.
        shell.Rebuilt += OnShellRebuilt;

        PreviewKeyDown += OnPushToTalkKeyDown;
        PreviewKeyUp += OnPushToTalkKeyUp;
        Deactivated += (_, _) => ReleasePushToTalk();
    }

    private void Bind()
    {
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnShellRebuilt()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (_boundChat is not null)
        {
            _boundChat.Messages.CollectionChanged -= OnMessagesChanged;
            _boundChat = null;
        }

        // The room window belongs to the old stack; a stale one would bind to disposed sessions.
        _roomWindow?.Close();

        _viewModel = _shell.MainViewModel;
        Bind();
    }

    private void OnPushToTalkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || !MatchesPushToTalk(e) || _pushToTalkHeld)
        {
            return;
        }

        _pushToTalkHeld = true;
        _viewModel.ActiveChat?.SetPushToTalk(true);
    }

    private void OnPushToTalkKeyUp(object sender, KeyEventArgs e)
    {
        if (!MatchesPushToTalk(e))
        {
            return;
        }

        ReleasePushToTalk();
    }

    private void ReleasePushToTalk()
    {
        if (!_pushToTalkHeld)
        {
            return;
        }

        _pushToTalkHeld = false;
        _viewModel.ActiveChat?.SetPushToTalk(false);
    }

    private bool MatchesPushToTalk(KeyEventArgs e)
    {
        if (_viewModel.ActiveChat is null)
        {
            return false;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var expected = _viewModel.PushToTalkKey;

        // Space is also a character: typing a message must not open the microphone.
        if (expected == Key.Space && Keyboard.FocusedElement is TextBoxBase)
        {
            return false;
        }

        return PushToTalkKeys.Matches(expected, key);
    }

    private void OpenCaptureTest_Click(object sender, RoutedEventArgs e)
    {
        if (_captureWindow is not null)
        {
            _captureWindow.Activate();
            return;
        }

        _captureWindow = _shell.CreateCaptureTestWindow();
        _captureWindow.Owner = this;
        _captureWindow.Closed += (_, _) => _captureWindow = null;
        _captureWindow.Show();
    }

    private void OpenRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_roomWindow is not null)
        {
            _roomWindow.Activate();
            return;
        }

        _roomWindow = _shell.CreateRoomWindow();
        _roomWindow.Owner = this;
        _roomWindow.Closed += (_, _) => _roomWindow = null;
        _roomWindow.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _shell.CreateSettingsWindow();
        _settingsWindow.Owner = this;
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowDialog();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveChat))
        {
            return;
        }

        if (_boundChat is not null)
        {
            _boundChat.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _boundChat = _viewModel.ActiveChat;
        _userScrolledUp = false;

        if (_boundChat is not null)
        {
            _boundChat.Messages.CollectionChanged += OnMessagesChanged;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            MessageInput.Focus();
            ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_userScrolledUp)
        {
            Dispatcher.BeginInvoke(new Action(ScrollToEnd), DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd()
    {
        if (MessagesList.Items.Count > 0)
        {
            MessagesList.ScrollIntoView(MessagesList.Items[^1]);
        }
    }

    private void MessagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            return;
        }

        _userScrolledUp = e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 2;
    }

    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.ActiveChat?.SendCommand.CanExecute(null) == true)
        {
            _viewModel.ActiveChat.SendCommand.Execute(null);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
    IntPtr hwnd,
    int dwAttribute,
    ref int pvAttribute,
    int cbAttribute);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        int useDarkMode = 1;

        DwmSetWindowAttribute(
            hwnd,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref useDarkMode,
            sizeof(int));
    }
}
