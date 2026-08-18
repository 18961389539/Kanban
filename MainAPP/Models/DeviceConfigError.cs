using Kanban.Collector.Core.Models;
using MainAPP.Services;
namespace MainAPP.Models;

/// <summary>
/// 设备配置校验错误（保存前聚合校验用）。携带定位信息（Device + 目标 Tab 索引），
/// 便于在错误对话框中点击后跳转到对应设备与选项卡。
/// </summary>
public sealed record DeviceConfigError
{
    /// <summary>出错的设备。</summary>
    public Device Device { get; init; } = null!;

    /// <summary>目标选项卡索引。</summary>
    public int TargetTabIndex { get; init; }

    /// <summary>面向用户的错误描述。</summary>
    public string Message { get; init; } = string.Empty;
}
