using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Helpers;
using MainAPP.Resources;

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
    private readonly AppSettings? _appSettings;

    public Device Device { get; }
    public DeviceRuntime Runtime { get; }

    /// <summary>实际节拍（秒/件）= 3600 / 目标节拍。目标为 0 时返回 0。</summary>
    public double ActualCycleSec => Runtime.TargetCycle > 0 ? 3600.0 / Runtime.TargetCycle : 0;

    /// <summary>目标节拍（秒/件）= 3600 / 目标产能（件/小时）。目标为 0 时返回 0。</summary>
    public double TargetCycleSec => Runtime.TargetCycle > 0 ? 3600.0 / Runtime.TargetCycle : 0;

    /// <summary>
    /// 真实节拍（秒/件）= 3600 / (目标产能 × 性能率)——性能率折损后的实际节拍。
    /// 2026-08-11 新增：卡片节拍显示"实际/目标"对比（原 ActualCycleSec 实为理论值）。
    /// </summary>
    public double RealCycleSec => Runtime.TargetCycle > 0 && Runtime.PerformanceRate > 0
        ? 3600.0 / (Runtime.TargetCycle * Runtime.PerformanceRate)
        : 0;

    /// <summary>节拍对比文本："18.8/9.0s"（实际/目标）；无数据时为 "—"。</summary>
    public string CycleText => RealCycleSec > 0 ? $"{RealCycleSec:0.0}/{TargetCycleSec:0.0}s" : "—";

    /// <summary>实际节拍是否慢于目标（用于节拍对比红色警示）。</summary>
    public bool IsCycleSlow => TargetCycleSec > 0 && RealCycleSec > TargetCycleSec;

    /// <summary>总产量 = OK + NG。</summary>
    public int TotalOutput => Runtime.TotalOkProduction + Runtime.TotalNgProduction;

    /// <summary>运行时长格式化（如 "5h 30m" / "30m 15s"）。</summary>
    public string RunTimeFormatted => FormatHelper.FormatDuration(Runtime.RunTime);
    /// <summary>报警时长格式化。</summary>
    public string AlarmTimeFormatted => FormatHelper.FormatDuration(Runtime.AlarmTime);
    /// <summary>待机时长格式化。</summary>
    public string PausedTimeFormatted => FormatHelper.FormatDuration(Runtime.PausedTime);
    /// <summary>离线时长格式化（仅统计，不参与 OEE）。</summary>
    public string OfflineTimeFormatted => FormatHelper.FormatDuration(Runtime.OfflineTime);

    /// <summary>综合停机时长（报警 + 待机）格式化。</summary>
    public string DowntimeFormatted => FormatHelper.FormatDuration(Runtime.AlarmTime + Runtime.PausedTime);

    /// <summary>
    /// 当前班次理论产能（件）= 目标产能（件/小时）× 当前班次小时数。
    /// 无班次配置/未配置 AppSettings 时返回 0（进度条隐藏）。
    /// </summary>
    public int ShiftTargetQuantity
    {
        get
        {
            var shift = FindCurrentShift();
            if (shift == null || Runtime.TargetCycle <= 0) return 0;
            var hours = (shift.EndTime - shift.StartTime).TotalHours;
            if (hours <= 0) hours += 24; // 跨天班次（如 20:00-08:00）
            return (int)Math.Round(Runtime.TargetCycle * hours);
        }
    }

    /// <summary>班次进度 0-1（本班次 OK / 班次理论产能，Clamp）。</summary>
    public double ShiftProgressRatio => ShiftTargetQuantity > 0
        ? Math.Clamp((double)Runtime.TotalOkProduction / ShiftTargetQuantity, 0, 1)
        : 0;

    /// <summary>班次进度文本："1,284 / 1,600"（不含单位）；无目标时为空。</summary>
    public string ShiftProgressText => ShiftTargetQuantity > 0
        ? $"{Runtime.TotalOkProduction:N0} / {ShiftTargetQuantity:N0}"
        : string.Empty;

    /// <summary>班次进度完整文本："1,284 / 1,600 件 · 80.3%"；无目标时为空。</summary>
    public string ShiftProgressFullText => ShiftTargetQuantity > 0
        ? $"{Runtime.TotalOkProduction:N0} / {ShiftTargetQuantity:N0} 件 · {ShiftProgressRatio:P1}"
        : string.Empty;

    /// <summary>是否有班次目标（进度条可见性）。</summary>
    public bool HasShiftTarget => ShiftTargetQuantity > 0;

    public LineDeviceItem(Device device, DeviceRuntime runtime, AppSettings? appSettings = null)
    {
        Device = device;
        Runtime = runtime;
        _appSettings = appSettings;
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
                OnPropertyChanged(nameof(TargetCycleSec));
                OnPropertyChanged(nameof(RealCycleSec));
                OnPropertyChanged(nameof(CycleText));
                OnPropertyChanged(nameof(IsCycleSlow));
                OnPropertyChanged(nameof(ShiftTargetQuantity)); // 目标产能变化影响班次目标
                OnPropertyChanged(nameof(ShiftProgressRatio));
                OnPropertyChanged(nameof(ShiftProgressText));
                OnPropertyChanged(nameof(ShiftProgressFullText));
                OnPropertyChanged(nameof(HasShiftTarget));
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
            case nameof(DeviceRuntime.OfflineTime):
                OnPropertyChanged(nameof(OfflineTimeFormatted));
                break;
            case nameof(DeviceRuntime.TotalOkProduction):
            case nameof(DeviceRuntime.TotalNgProduction):
                OnPropertyChanged(nameof(TotalOutput));
                // 班次进度（本班次 OK 变化；班次切换瞬间产量清零也经此路径刷新目标）
                OnPropertyChanged(nameof(ShiftProgressRatio));
                OnPropertyChanged(nameof(ShiftProgressText));
                OnPropertyChanged(nameof(ShiftProgressFullText));
                break;
        }
    }

    /// <summary>当前时刻所属班次（无配置/不属于任何班次时返回 null）。
    /// 委托 HistoryQueryHelper 单源实现（Contains 语义一致）。</summary>
    private ShiftConfig? FindCurrentShift()
    {
        var shifts = _appSettings?.GetShiftsSnapshot(); // P0-1 修复 2026-09-02：锁内快照，禁止直接枚举
        if (shifts == null || shifts.Count == 0) return null;
        return ViewModels.HistoryQueryHelper.FindCurrentShift(shifts, DateTime.Now.TimeOfDay).Shift;
    }

    /// <summary>当前触发的报警名称（StartTime 已置、EndTime 为空），顿号拼接；无则空字符串。</summary>
    public string ActiveAlarmText
    {
        get
        {
            var names = Device.Alarms
                .Where(a => a.StartTime != default && a.EndTime == default)
                .Select(Services.AlarmNameLocalizer.Resolve)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            return names.Length == 0 ? string.Empty : string.Join("、", names);
        }
    }

    /// <summary>当前触发报警的开始时间（HH:mm）；无触发报警返回空字符串。</summary>
    public string AlarmStartTimeDisplay
    {
        get
        {
            var a = Device.Alarms.FirstOrDefault(x => x.StartTime != default && x.EndTime == default);
            return a == null || a.StartTime == default ? string.Empty : a.StartTime.ToString("HH:mm");
        }
    }

    /// <summary>
    /// 当前触发报警的持续时长（如 "19s" / "2m 15s"）；无触发报警返回空字符串。
    /// 2026-08-11 新增：卡片报警行显示 "⚠ 主电机过载 · 19s"。
    /// </summary>
    public string ActiveAlarmDurationText
    {
        get
        {
            var a = Device.Alarms.FirstOrDefault(x => x.StartTime != default && x.EndTime == default);
            if (a == null || a.StartTime == default) return string.Empty;
            return FormatHelper.FormatDuration((DateTime.Now - a.StartTime).TotalSeconds);
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
            if (counts.TryGetValue(DefectSeverity.Critical, out var c) && c > 0) parts.Add(string.Format(Strings.F058, c));
            if (counts.TryGetValue(DefectSeverity.Major, out var m) && m > 0) parts.Add(string.Format(Strings.F054, m));
            if (counts.TryGetValue(DefectSeverity.Minor, out var n) && n > 0) parts.Add(string.Format(Strings.F219, n));
            return parts.Count == 0 ? string.Empty : Strings.M262 + string.Join(" ", parts);
        }
    }

    /// <summary>运行时状态/缺陷集合变化后，通知派生展示文本刷新（由 VM 在 Runtime.PropertyChanged 时调用）。</summary>
    public void RefreshTransientTexts()
    {
        OnPropertyChanged(nameof(ActiveAlarmText));
        OnPropertyChanged(nameof(AlarmStartTimeDisplay));
        OnPropertyChanged(nameof(ActiveAlarmDurationText));
        OnPropertyChanged(nameof(DefectSummaryText));
    }


}
