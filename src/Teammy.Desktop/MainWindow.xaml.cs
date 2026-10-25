using System.Windows;
using Teammy.Presentation.ViewModels;

namespace Teammy.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(WorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
