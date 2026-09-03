using System.Windows;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class CaptureTestWindow : Window
{
    private readonly CaptureTestViewModel _viewModel;

    public CaptureTestWindow(CaptureTestViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel.StopCommand.CanExecute(null))
        {
            _viewModel.StopCommand.Execute(null);
        }

        base.OnClosed(e);
    }
}
