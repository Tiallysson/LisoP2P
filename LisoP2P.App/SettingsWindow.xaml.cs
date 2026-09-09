using System.Windows;
using System.Windows.Input;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.Saved += () =>
        {
            DialogResult = true;
            Close();
        };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// "Definir" arms the capture and the very next key press becomes the push-to-talk key. The
    /// event is swallowed so pressing Space or Enter does not also click a button behind it.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsCapturingKey)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        _viewModel.CaptureKey(key);
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
