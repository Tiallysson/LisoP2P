using System.Windows;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
