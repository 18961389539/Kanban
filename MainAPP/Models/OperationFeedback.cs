using CommunityToolkit.Mvvm.ComponentModel;

namespace MainAPP.Models;

/// <summary>
/// 页面内操作反馈的统一状态。Growl 用于瞬时通知，这个对象用于在当前页面保留
/// "正在处理 / 已完成 / 失败" 的上下文，避免用户错过通知后无法判断当前状态。
/// </summary>
public enum OperationFeedbackKind
{
    None,
    Working,
    Success,
    Warning,
    Error,
}

public sealed partial class OperationFeedback : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private OperationFeedbackKind _kind = OperationFeedbackKind.None;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public void Clear()
    {
        Message = string.Empty;
        Kind = OperationFeedbackKind.None;
    }

    public void Working(string message) => Set(OperationFeedbackKind.Working, message);

    public void Success(string message) => Set(OperationFeedbackKind.Success, message);

    public void Warning(string message) => Set(OperationFeedbackKind.Warning, message);

    public void Error(string message) => Set(OperationFeedbackKind.Error, message);

    private void Set(OperationFeedbackKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }
}