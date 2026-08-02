using System.Windows;
using System.Windows.Input;
using LicenseManager.ViewModels;

namespace LicenseManager.Views;

/// <summary>
/// 激活对话框：在试用过期或用户主动激活时弹出。
/// 激活成功后通过 DialogResult=true 通知调用方。
/// </summary>
public partial class ActivationDialog : Window
{
    public ActivationDialog(ActivationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ActivationSucceeded += OnActivationSucceeded;
        // P2-3：Esc 键关闭对话框（与取消按钮等效）
        KeyDown += OnDialogKeyDown;
    }

    private void OnActivationSucceeded()
    {
        Dispatcher.Invoke(() =>
        {
            DialogResult = true;
            Close();
        });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Esc 键关闭对话框（与点击取消按钮等效）。
    /// 回车键触发激活（与点击激活按钮等效，提升键鼠效率）。
    /// </summary>
    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // 回车触发激活（仅当激活命令可执行时）
            if (DataContext is ActivationViewModel vm && vm.ActivateCommand.CanExecute(null))
            {
                vm.ActivateCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
