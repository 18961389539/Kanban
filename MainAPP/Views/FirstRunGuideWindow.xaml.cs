using System.Windows;
using MainAPP.ViewModels;

namespace MainAPP.Views;

public partial class FirstRunGuideWindow : Window
{
    public FirstRunGuideWindow(FirstRunGuideViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
