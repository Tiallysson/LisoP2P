using System.Windows;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.Completed += () =>
        {
            DialogResult = true;
            Close();
        };

        Loaded += (_, _) =>
        {
            NicknameInput.Focus();
            NicknameInput.SelectAll();
        };
    }
}
