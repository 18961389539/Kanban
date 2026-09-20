using System.Windows;
using System.Windows.Input;
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

    /// <summary>
    /// Enter 推进向导：最后一步回车=完成，其余步回车=下一步（NextCommand 内部校验当前步；
    /// 按钮无法随步骤切换 IsDefault，故在窗口级处理）。Esc 由取消按钮 IsCancel 负责。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_viewModel.IsLastStep)
            _viewModel.FinishCommand.Execute(null);
        else
            _viewModel.NextCommand.Execute(null);
        e.Handled = true;
    }
}