using System.Windows;
using LisoP2P.Net;

namespace LisoP2P.App;

public partial class StartupErrorWindow : Window
{
    private readonly Action _openSettings;

    /// <summary>True when the user wants the startup path to resolve the ports and try again.</summary>
    public bool ShouldRetry { get; private set; }

    public StartupErrorWindow(IReadOnlyList<PortCheck> conflicts, Action openSettings)
    {
        InitializeComponent();
        _openSettings = openSettings;
        ConflictList.ItemsSource = conflicts;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();

        // Coming back from settings means new ports were saved; retrying is the only useful move.
        ShouldRetry = true;
        Close();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = true;
        Close();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = false;
        Close();
    }
}
