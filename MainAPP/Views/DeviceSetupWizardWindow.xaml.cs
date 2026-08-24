using System.Windows;
using MainAPP.ViewModels;

namespace MainAPP.Views;

public partial class DeviceSetupWizardWindow : Window
{
    private readonly DeviceSetupWizardViewModel _viewModel;

    public DeviceSetupWizardWindow(DeviceSetupWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;
        _viewModel.Cancelled += OnCancelled;
        Closed += OnClosed;
    }

    private void OnCompleted() => DialogResult = true;

    private void OnCancelled() => DialogResult = false;

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Completed -= OnCompleted;
        _viewModel.Cancelled -= OnCancelled;
        Closed -= OnClosed;
    }
}