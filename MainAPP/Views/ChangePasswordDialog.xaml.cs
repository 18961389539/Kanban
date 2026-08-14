using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MainAPP.Resources;
using MainAPP.Services;

namespace MainAPP.Views;

/// <summary>
/// 修改/重置密码对话框：新密码（强度条）+ 确认密码（一致性校验）。
/// 供"首次登录强制改密"与"用户管理页重置密码"复用（替换单框密码输入）。
/// 密码经 PasswordBox 获取，不绑定 ViewModel（避免明文留在内存）。
/// </summary>
public partial class ChangePasswordDialog : Window
{
    public string Title { get; }
    public string Message { get; }

    /// <summary>确认成功后的新密码（DialogResult=true 时有效）。</summary>
    public string Password => NewPasswordBox.Password;

    public ChangePasswordDialog(string title, string message)
    {
        Title = title;
        Message = message;
        DataContext = this;
        InitializeComponent();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        // 强度条（长度 + 字符类型）
        var level = PasswordPolicy.EvaluateStrength(NewPasswordBox.Password);
        StrengthText.Text = PasswordPolicy.StrengthText(level);
        var (color, widthPct) = level switch
        {
            2 => (Brushes.LightGreen, 1.0),
            1 => (Brushes.Gold, 0.66),
            _ => (Brushes.IndianRed, 0.33),
        };
        StrengthBar.Background = color;
        StrengthBar.Width = ActualWidth * widthPct;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm_Click(sender, e);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var password = NewPasswordBox.Password;
        if (!PasswordPolicy.IsLongEnough(password))
        {
            ErrorText.Text = Strings.M364;
            return;
        }
        if (password != ConfirmPasswordBox.Password)
        {
            ErrorText.Text = Strings.M365;
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
