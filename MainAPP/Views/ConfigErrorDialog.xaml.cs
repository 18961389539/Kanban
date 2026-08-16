using System.Windows;
using System.Windows.Input;
using Kanban.Collector.Core.Models;
using MainAPP.Models;

namespace MainAPP.Views;

/// <summary>
/// 保存前聚合配置错误的展示对话框。双击某项错误即选中并关闭，
/// 由调用方（DeviceManagerViewModel）负责跳转到对应设备与选项卡。
/// </summary>
public partial class ConfigErrorDialog : Window
{
    public IReadOnlyList<DeviceConfigError> Errors { get; }

    public DeviceConfigError? SelectedError { get; private set; }

    public ConfigErrorDialog(IReadOnlyList<DeviceConfigError> errors)
    {
        InitializeComponent();
        Errors = errors;
        DataContext = this;
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ErrorList.SelectedItem is DeviceConfigError err)
        {
            SelectedError = err;
            DialogResult = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Esc 关闭对话框（IsCancel=True 的按钮已自动处理，此处兜底确保 ListBox 焦点下也生效）。
    /// Enter 选中当前错误项并跳转（与双击等效）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ErrorList.SelectedItem is DeviceConfigError err)
            {
                SelectedError = err;
                DialogResult = true;
                e.Handled = true;
            }
        }
    }
}
