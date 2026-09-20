using System.Windows;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IDialogService 的内存测试桩：记录所有通知消息与模态框调用，
/// Show 返回可配置结果，便于断言设置页 Save 的分支行为（确认框 Yes/No、各通知路径）。
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>模态 MessageBox 的返回结果（默认 Yes）。</summary>
    public MessageBoxResult ShowResult { get; set; } = MessageBoxResult.Yes;

    /// <summary>所有 Show 调用的 (消息, 标题, 按钮, 图标) 记录。</summary>
    public List<(string Message, string Title, MessageBoxButton Buttons, MessageBoxImage Icon)> ShowCalls { get; } = new();

    public List<string> Success { get; } = new();
    public List<string> Warning { get; } = new();
    public List<string> Error { get; } = new();
    public List<string> Info { get; } = new();

    /// <summary>ShowSaveFileDialog 应返回的完整路径；null 表示用户取消。</summary>
    public string? SaveFilePath { get; set; }

    /// <summary>ShowOpenFileDialog 应返回的完整路径；null 表示用户取消。</summary>
    public string? OpenFilePath { get; set; }

    /// <summary>每次 ShowConfigErrors 调用传入的错误列表（按调用顺序记录）。</summary>
    public List<IReadOnlyList<DeviceConfigError>> ConfigErrorCalls { get; } = new();

    /// <summary>ShowConfigErrors 应返回的错误（模拟用户点击某项定位）；null 表示关闭不定位。</summary>
    public DeviceConfigError? ShowConfigErrorsResult { get; set; }

    /// <summary>ShowPasswordInput 应返回的密码文本；null 表示用户取消。</summary>
    public string? PasswordInputResult { get; set; }

    /// <summary>每次 ShowPasswordInput 调用传入的 (标题, 消息) 记录。</summary>
    public List<(string Title, string Message)> PasswordInputCalls { get; } = new();

    /// <summary>每次 ShowWorkOrderEditor 调用传入的 (template, availableDevices) 记录。</summary>
    public List<(WorkOrder? Template, IReadOnlyList<(string Id, string Name)>? Devices)> WorkOrderEditorCalls { get; } = new();

    /// <summary>ShowWorkOrderEditor 应返回的工单副本；null 表示用户取消。</summary>
    public WorkOrder? WorkOrderEditorResult { get; set; }

    /// <summary>每次 ShowWorkOrderContinue 调用传入的 (completed, selectable) 记录。</summary>
    public List<(WorkOrder Completed, IReadOnlyList<WorkOrder> Selectable)> ContinueCalls { get; } = new();

    /// <summary>ShowWorkOrderContinue 应返回的选择；默认稍后再说。</summary>
    public WorkOrderContinueChoice ContinueResult { get; set; } = WorkOrderContinueChoice.Dismissed;

    public List<string> SaveFileDialogTitles { get; } = new();
    public List<string> OpenFileDialogTitles { get; } = new();

    public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        ShowCalls.Add((message, title, buttons, icon));
        return ShowResult;
    }

    public void NotifySuccess(string message) => Success.Add(message);
    public void NotifyWarning(string message) => Warning.Add(message);
    public void NotifyError(string message) => Error.Add(message);
    public void NotifyInfo(string message) => Info.Add(message);

    public string? ShowSaveFileDialog(string title, string defaultFileName, string filter)
    {
        SaveFileDialogTitles.Add(title);
        return SaveFilePath;
    }

    public string? ShowOpenFileDialog(string title, string filter)
    {
        OpenFileDialogTitles.Add(title);
        return OpenFilePath;
    }

    public DeviceConfigError? ShowConfigErrors(IReadOnlyList<DeviceConfigError> errors)
    {
        ConfigErrorCalls.Add(errors);
        return ShowConfigErrorsResult;
    }

    public string? ShowPasswordInput(string title, string message)
    {
        PasswordInputCalls.Add((title, message));
        return PasswordInputResult;
    }

    public WorkOrder? ShowWorkOrderEditor(WorkOrder? template, IReadOnlyList<(string Id, string Name)>? availableDevices = null)
    {
        WorkOrderEditorCalls.Add((template, availableDevices));
        return WorkOrderEditorResult;
    }

    public WorkOrderContinueChoice ShowWorkOrderContinue(WorkOrder completed, IReadOnlyList<WorkOrder> selectableOrders)
    {
        ContinueCalls.Add((completed, selectableOrders ?? Array.Empty<WorkOrder>()));
        return ContinueResult;
    }
}
