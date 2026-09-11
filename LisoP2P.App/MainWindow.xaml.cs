using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using LisoP2P.App.Services;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class MainWindow : Window
{
    private readonly IAppShell _shell;
    private MainViewModel _viewModel;
    private CaptureTestWindow? _captureWindow;
    private SettingsWindow? _settingsWindow;
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
        _viewModel.SettingsRequested += OpenSettings;
        _viewModel.CaptureTestRequested += OpenCaptureTest;
    }

    private void Unbind()
    {
        _viewModel.SettingsRequested -= OpenSettings;
        _viewModel.CaptureTestRequested -= OpenCaptureTest;
    }

    private void OnShellRebuilt()
    {
        Unbind();

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
        _viewModel.SetPushToTalk(true);
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
        _viewModel.SetPushToTalk(false);
    }

    private bool MatchesPushToTalk(KeyEventArgs e)
    {
        if (_viewModel.ActiveConversation is null)
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

    private void OpenCaptureTest()
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

    private void OpenSettings()
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
