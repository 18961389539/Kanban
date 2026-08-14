using System.Windows;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 用户管理页面。密码经 PasswordBox 获取（不绑定 ViewModel，避免明文留在内存）。
/// </summary>
public partial class UserManagerView : System.Windows.Controls.UserControl
{
    public UserManagerView()
    {
        InitializeComponent();
    }

    /// <summary>添加用户：把 PasswordBox 密码传给 ViewModel 命令。</summary>
    private void OnAddUserClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserManagerViewModel vm)
        {
            vm.AddUserCommand.Execute(NewPasswordBox.Password);
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            vm.UpdatePasswordInput("", "");
        }
    }

    /// <summary>密码/确认框内容变化：实时同步强度条与一致性到 ViewModel。</summary>
    private void OnNewPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserManagerViewModel vm)
        {
            vm.UpdatePasswordInput(NewPasswordBox.Password, ConfirmPasswordBox.Password);
        }
    }
}
