using LisoP2P.App.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LisoP2P.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<CaptureTestWindow> _captureWindowFactory;
    private ChatViewModel? _boundChat;
    private CaptureTestWindow? _captureWindow;
    private bool _userScrolledUp;

    public MainWindow(MainViewModel viewModel, Func<CaptureTestWindow> captureWindowFactory)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _captureWindowFactory = captureWindowFactory;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OpenCaptureTest_Click(object sender, RoutedEventArgs e)
    {
        if (_captureWindow is not null)
        {
            _captureWindow.Activate();
            return;
        }

        _captureWindow = _captureWindowFactory();
        _captureWindow.Owner = this;
        _captureWindow.Closed += (_, _) => _captureWindow = null;
        _captureWindow.Show();
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
