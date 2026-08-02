using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Microsoft.Win32;
using System.Windows;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using MainAPP.Views;

namespace MainAPP.Services;

/// <summary>
/// 对话框与通知服务抽象：解耦 ViewModel 与 HandyControl 控件。
/// ViewModel 通过此接口弹模态确认框和非模态通知（Growl），
/// 单元测试时可注入 FakeDialogService 避免依赖 UI 容器。
/// </summary>
public interface IDialogService
{
    /// <summary>模态消息框，返回用户点击的按钮。</summary>
    MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon);

    /// <summary>成功通知（绿色，非模态）。</summary>
    void NotifySuccess(string message);

    /// <summary>警告通知（黄色，非模态）。</summary>
    void NotifyWarning(string message);

    /// <summary>错误通知（红色，非模态）。</summary>
    void NotifyError(string message);

    /// <summary>信息通知（蓝色，非模态）。</summary>
    void NotifyInfo(string message);

    /// <summary>打开"保存文件"对话框，返回用户选择的完整路径；用户取消返回 null。</summary>
    string? ShowSaveFileDialog(string title, string defaultFileName, string filter);

    /// <summary>打开"打开文件"对话框，返回用户选择的完整路径；用户取消返回 null。</summary>
    string? ShowOpenFileDialog(string title, string filter);

    /// <summary>
    /// 展示聚合后的配置校验错误列表，用户可双击某项以定位到对应设备/选项卡。
    /// 返回被点击的错误（关闭/取消返回 null）。
    /// </summary>
    DeviceConfigError? ShowConfigErrors(IReadOnlyList<DeviceConfigError> errors);

    /// <summary>
    /// 密码输入对话框（密码字符遮蔽）。返回用户输入的密码文本；用户取消返回 null。
    /// </summary>
    string? ShowPasswordInput(string title, string message);

    /// <summary>
    /// 工单编辑对话框。传入 template 时为编辑模式（预填字段），null 为新增模式。
    /// 可选参数 availableDevices 提供可选设备列表（用于设备下拉）。
    /// 返回用户确认后的工单副本；用户取消返回 null。
    /// </summary>
    WorkOrder? ShowWorkOrderEditor(WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null);
}

/// <summary>
/// IDialogService 的 HandyControl 实现。
/// MessageBox 用 HC 版本以获得深色主题样式；通知用 HC Growl（需 MainWindow 启动期注册 GrowlParent 容器）。
/// </summary>
public class DialogService : IDialogService
{
    public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        return HandyControl.Controls.MessageBox.Show(message, title, buttons, icon);
    }

    public void NotifySuccess(string message)
        => HandyControl.Controls.Growl.Success(message);

    public void NotifyWarning(string message)
        => HandyControl.Controls.Growl.Warning(message);

    public void NotifyError(string message)
        => HandyControl.Controls.Growl.Error(message);

    public void NotifyInfo(string message)
        => HandyControl.Controls.Growl.Info(message);

    public string? ShowSaveFileDialog(string title, string defaultFileName, string filter)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter,
            AddExtension = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? ShowOpenFileDialog(string title, string filter)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public DeviceConfigError? ShowConfigErrors(IReadOnlyList<DeviceConfigError> errors)
    {
        var window = new ConfigErrorDialog(errors);
        if (Application.Current?.MainWindow is Window owner)
            window.Owner = owner;
        return window.ShowDialog() == true ? window.SelectedError : null;
    }

    public string? ShowPasswordInput(string title, string message)
    {
        var window = new PasswordInputDialog(title, message);
        if (Application.Current?.MainWindow is Window owner)
            window.Owner = owner;
        return window.ShowDialog() == true ? window.Password : null;
    }

    public WorkOrder? ShowWorkOrderEditor(WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null)
    {
        var window = new WorkOrderEditDialog(template, availableDevices);
        if (Application.Current?.MainWindow is Window owner)
            window.Owner = owner;
        return window.ShowDialog() == true ? window.Result : null;
    }
}
