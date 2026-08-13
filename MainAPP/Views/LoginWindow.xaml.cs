using System.Windows;
using System.Windows.Input;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 登录窗口。密码通过 PasswordBox 获取（不绑定到 ViewModel，避免明文留在内存）。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginWindow(LoginViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>登录按钮点击：把密码传给 ViewModel 命令。</summary>
    private void OnLoginClick(object sender, RoutedEventArgs e)
    {
        _viewModel.LoginCommand.Execute(PasswordBox.Password);
        if (_viewModel.LoginSucceeded)
        {
            DialogResult = true;
            Close();
        }
    }

    /// <summary>密码框回车：触发登录。</summary>
    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnLoginClick(sender, e);
        }
    }

    /// <summary>取消按钮：直接关闭窗口（DialogResult 保持 null/false）。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
