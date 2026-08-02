using System.Windows;
using System.Windows.Input;

namespace MainAPP.Views;

/// <summary>
/// 密码输入对话框。ShowDialog 返回 true 时通过 Password 属性获取用户输入的密码；
/// 取消/关闭返回 false，Password 为空。
/// </summary>
public partial class PasswordInputDialog : Window
{
    public PasswordInputDialog(string title, string message)
    {
        InitializeComponent();
        // XAML 中 Title="{Binding Title}" Message="{Binding Message}" 依赖 DataContext，
        // 必须设为 this 才能解析绑定，否则对话框标题与消息为空（用户看不到提示）。
        DataContext = this;
        Title = title;
        Message = message;
    }

    public string Message { get; set; } = string.Empty;

    /// <summary>用户确认后返回的密码文本。</summary>
    public string Password => PasswordBox.Password;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Enter 由 IsDefault=True 的确定按钮自动处理；Esc 由 IsCancel=True 的取消按钮自动处理。
        // 不在此处重复调用 Confirm_Click：IsDefault 按钮已触发一次，二次调用会在窗口关闭后
        // 再次设置 DialogResult/Close，抛 InvalidOperationException。
        e.Handled = false;
    }
}
