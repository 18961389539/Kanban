using System.Windows;
using MainAPP.ViewModels;

namespace MainAPP.Views;

/// <summary>
/// 用户管理页面。密码通过 PasswordBox 获取（不绑定到 ViewModel，避免明文留在内存）。
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
        }
    }

    /// <summary>重置密码：弹密码输入框，传给 ViewModel 命令。</summary>
    private void OnResetPasswordClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserManagerViewModel vm && vm.SelectedUser is not null)
        {
            // 复用现有 PasswordInputDialog 收集新密码
            var dialog = new PasswordInputDialog(MainAPP.Resources.Strings.M335, MainAPP.Resources.Strings.M335);
            if (Application.Current?.MainWindow is Window owner)
                dialog.Owner = owner;
            if (dialog.ShowDialog() == true)
            {
                vm.ResetPasswordCommand.Execute(dialog.Password);
            }
        }
    }
}
