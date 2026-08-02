using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Helpers;

namespace MainAPP.Models;

/// <summary>
/// 产线页设备项：包装 Device + DeviceRuntime，供 UI 直接绑定。
/// 转发 Runtime 的 PropertyChanged 以通知派生属性（ActualCycleSec / FormattedXxx）变更。
/// 实现 IDisposable：从 LineDevices 移除时由 VM 调用 Dispose 退订内部事件订阅，
/// 防止 Runtime 存活期间 LineDeviceItem 因事件引用无法回收。
/// </summary>
public partial class LineDeviceItem : ObservableObject, IDisposable
{
    private bool _disposed;

    public Device Device { get; }
    public DeviceRuntime Runtime { get; }

    /// <summary>实际节拍（秒/件）= 3600 / 目标节拍。目标为 0 时返回 0。</summary>
    public double ActualCycleSec => Runtime.TargetCycle > 0 ? 3600.0 / Runtime.TargetCycle : 0;

    /// <summary>总产量 = OK + NG。</summary>
    public int TotalOutput => Runtime.TotalOkProduction + Runtime.TotalNgProduction;

    /// <summary>运行时长格式化（如 "5h 30m" / "30m 15s"）。</summary>
    public string RunTimeFormatted => FormatHelper.FormatDuration(Runtime.RunTime);
    /// <summary>报警时长格式化。</summary>
    public string AlarmTimeFormatted => FormatHelper.FormatDuration(Runtime.AlarmTime);
    /// <summary>待机时长格式化。</summary>
    public string PausedTimeFormatted => FormatHelper.FormatDuration(Runtime.PausedTime);

    /// <summary>综合停机时长（报警 + 待机）格式化。</summary>
    public string DowntimeFormatted => FormatHelper.FormatDuration(Runtime.AlarmTime + Runtime.PausedTime);

    public LineDeviceItem(Device device, DeviceRuntime runtime)
    {
        Device = device;
        Runtime = runtime;
        Runtime.PropertyChanged += OnRuntimePropertyChanged;
    }

    /// <summary>
    /// 退订内部事件订阅，避免 Runtime 存活时 LineDeviceItem 因事件引用无法回收。
    /// VM 在 TryRemoveLineDevice / SyncLineDevices 移除项前调用。
    /// 注意：仅退订本类内部的订阅；VM 自身通过 item.Runtime.PropertyChanged +=/-= 注册的
    /// 独立订阅由 VM 自行管理，不受此 Dispose 影响。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runtime.PropertyChanged -= OnRuntimePropertyChanged;
    }

    /// <summary>
    /// 转发 Runtime 属性变更，触发 LineDeviceItem 派生属性的 PropertyChanged。
    /// 仅转发影响派生属性的源属性，避免无谓通知。
    /// 注意：DeviceRuntime.SyncTargetCycle 不 raise TargetCycle，改 raise PerformanceRate/OEE，
    /// 故监听 PerformanceRate 以同步 ActualCycleSec。
    /// </summary>
    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceRuntime.TargetCycle):
            case nameof(DeviceRuntime.PerformanceRate): // SyncTargetCycle 触发
                OnPropertyChanged(nameof(ActualCycleSec));
                break;
            case nameof(DeviceRuntime.RunTime):
                OnPropertyChanged(nameof(RunTimeFormatted));
                break;
            case nameof(DeviceRuntime.AlarmTime):
                OnPropertyChanged(nameof(AlarmTimeFormatted));
                OnPropertyChanged(nameof(DowntimeFormatted));
                break;
            case nameof(DeviceRuntime.PausedTime):
                OnPropertyChanged(nameof(PausedTimeFormatted));
                OnPropertyChanged(nameof(DowntimeFormatted));
                break;
            case nameof(DeviceRuntime.TotalOkProduction):
            case nameof(DeviceRuntime.TotalNgProduction):
                OnPropertyChanged(nameof(TotalOutput));
                break;
        }
    }

    /// <summary>当前触发的报警名称（StartTime 已置、EndTime 为空），顿号拼接；无则空字符串。</summary>
    public string ActiveAlarmText
    {
        get
        {
            var names = Device.Alarms
                .Where(a => a.StartTime != default && a.EndTime == default)
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            return names.Length == 0 ? string.Empty : string.Join("、", names);
        }
    }

    /// <summary>缺陷按严重度统计摘要（如 "缺陷 严重2 一般1"）；无缺陷则空字符串。</summary>
    public string DefectSummaryText
    {
        get
        {
            if (Device.Defects.Count == 0) return string.Empty;
            var counts = Device.Defects
                .GroupBy(d => d.Severity)
                .ToDictionary(g => g.Key, g => g.Count());
            List<string> parts = [];
            if (counts.TryGetValue(DefectSeverity.Critical, out var c) && c > 0) parts.Add($"严重{c}");
            if (counts.TryGetValue(DefectSeverity.Major, out var m) && m > 0) parts.Add($"一般{m}");
            if (counts.TryGetValue(DefectSeverity.Minor, out var n) && n > 0) parts.Add($"轻微{n}");
            return parts.Count == 0 ? string.Empty : "缺陷 " + string.Join(" ", parts);
        }
    }

    /// <summary>运行时状态/缺陷集合变化后，通知派生展示文本刷新（由 VM 在 Runtime.PropertyChanged 时调用）。</summary>
    public void RefreshTransientTexts()
    {
        OnPropertyChanged(nameof(ActiveAlarmText));
        OnPropertyChanged(nameof(DefectSummaryText));
    }


}
