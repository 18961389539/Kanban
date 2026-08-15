using System.ComponentModel;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;

namespace MainAPP.ViewModels;

/// <summary>
/// 设备管理器子 ViewModel 访问父级（DeviceManagerViewModel）共享状态的抽象。
/// 报警/缺陷/计数报警子 VM 通过此接口获取选中设备、共享加载标志并回写脏标记，
/// 避免子 VM 直接依赖具体父 VM 类型造成循环引用。
/// </summary>
public interface IDeviceManagerHost : INotifyPropertyChanged
{
    /// <summary>当前选中的设备（子 VM 据此操作对应子集合）。</summary>
    Device? SelectedDevice { get; }

    /// <summary>
    /// PLC 写入中标志（跨子 VM 共享）：计数报警清零期间由子 VM 置位，
    /// 驱动父级加载覆盖层与各子 VM 的命令可用状态刷新。
    /// </summary>
    bool IsLoading { get; set; }

    /// <summary>当前 PLC 是否在线；断线时禁止子 Tab 的 PLC 写入命令。</summary>
    bool IsPlcConnected { get; }

    /// <summary>将子 Tab 的 PLC 异步结果汇总到设备参数页状态栏。</summary>
    void ReportPlcOperation(PlcOpResult result);

    /// <summary>标记 PLC 操作开始（供子 Tab 在 IsLoading 置位时同步状态栏为进行中）。</summary>
    void ReportPlcOperationStarted();

    /// <summary>标记设备配置存在未保存改动（由子 VM 的增删/编辑操作回调）。</summary>
    void MarkDirty();
}
