using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Contracts.Metrics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MainAPP.Helpers;
using MainAPP.Resources;
using MainAPP.ViewModels;

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
    private readonly Func<DateTime> _clock;

    public Device Device { get; }
    public DeviceRuntime Runtime { get; }

    /// <summary>运行期间平均周期（秒/件），与主页实际周期同源；数据不足时为 0。</summary>
    public double ActualCycleSec => RealCycleSec;

    /// <summary>目标周期（秒/件）= 3600 / 目标产能（件/小时）。</summary>
    public double TargetCycleSec => SnapshotMetrics.CycleSeconds(Runtime.TargetCycle);

    /// <summary>
    /// 运行期间平均周期（秒/件）。用产量/运行时长换算，不经性能率 100% 封顶，
    /// 超产时可以快于目标。
    /// </summary>
    public double RealCycleSec => SnapshotMetrics.AverageCycleSeconds(
        Runtime.RunTime, Runtime.TotalOkProduction, Runtime.TotalNgProduction);

    /// <summary>平均周期对比文本："18.8/9.0s"（实际/目标）；无数据时为 "—"。</summary>
    public string CycleText => RealCycleSec > 0 && TargetCycleSec > 0
        ? $"{RealCycleSec:0.0}/{TargetCycleSec:0.0}s"
        : "—";

    /// <summary>平均周期是否慢于目标（用于周期对比红色警示）。</summary>
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
    /// 当前班次整班理论产能（件）= 目标产能 × 班次总时长。进度条分母用
    /// <see cref="ShiftExpectedQuantity"/>。
    /// </summary>
    public int ShiftTargetQuantity
    {
        get
        {
            var (shift, start, end) = ResolveCurrentShift();
            if (shift == null || Runtime.TargetCycle <= 0) return 0;
            return SnapshotMetrics.ExpectedOutput(Runtime.TargetCycle, (end - start).TotalHours);
        }
    }

    /// <summary>到此刻应产件数 = 目标产能 × 班次已过小时；班次刚开始时为 0。</summary>
    public int ShiftExpectedQuantity
    {
        get
        {
            var (shift, start, _) = ResolveCurrentShift();
            if (shift == null || Runtime.TargetCycle <= 0) return 0;
            return SnapshotMetrics.ExpectedOutput(Runtime.TargetCycle, (_clock() - start).TotalHours);
        }
    }

    /// <summary>班次良品达成比 = 本班次 OK / 到此刻应产；应产为 0 时返回 0。可大于 1（超额）。</summary>
    public double ShiftProgressRatio
    {
        get
        {
            var expected = ShiftExpectedQuantity;
            return expected > 0 ? (double)Runtime.TotalOkProduction / expected : 0;
        }
    }

    /// <summary>进度条填充 0–1（超额时停在满格，百分比仍显示真实达成）。</summary>
    public double ShiftProgressBarValue => Math.Clamp(ShiftProgressRatio, 0, 1);

    /// <summary>良品达成文本："1,284 / 1,600 · 80.3%"；应产尚未形成时分母为 "—"。</summary>
    public string ShiftProgressText
    {
        get
        {
            if (!HasShiftTarget) return string.Empty;
            var expected = ShiftExpectedQuantity;
            var ok = $"{Runtime.TotalOkProduction:N0}";
            if (expected <= 0) return $"{ok} / —";
            return $"{ok} / {expected:N0} · {ShiftProgressRatio:P1}";
        }
    }

    /// <summary>班次达成完整文本（含单位）。</summary>
    public string ShiftProgressFullText
    {
        get
        {
            if (!HasShiftTarget) return string.Empty;
            var expected = ShiftExpectedQuantity;
            var ok = $"{Runtime.TotalOkProduction:N0}";
            if (expected <= 0) return $"{ok} / —";
            return $"{ok} / {expected:N0} 件 · {ShiftProgressRatio:P1}";
        }
    }

    /// <summary>是否有班次与目标产能（进度条可见性，不要求已过时间）。</summary>
    public bool HasShiftTarget
    {
        get
        {
            var (shift, _, _) = ResolveCurrentShift();
            return shift != null && Runtime.TargetCycle > 0;
        }
    }

    public LineDeviceItem(
        Device device,
        DeviceRuntime runtime,
        AppSettings? appSettings = null,
        Func<DateTime>? clock = null)
    {
        Device = device;
        Runtime = runtime;
        _appSettings = appSettings;
        _clock = clock ?? (() => DateTime.Now);
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
    /// 注意：DeviceRuntime.SyncTargetCycle 不 raise TargetCycle，改 raise PerformanceRate/OEE。
    /// </summary>
    private void OnRuntimePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceRuntime.TargetCycle):
            case nameof(DeviceRuntime.PerformanceRate):
                NotifyCycleChanged();
                RefreshShiftProgress();
                break;
            case nameof(DeviceRuntime.RunTime):
                OnPropertyChanged(nameof(RunTimeFormatted));
                NotifyCycleChanged();
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
                NotifyCycleChanged();
                RefreshShiftProgress();
                break;
        }
    }

    private void NotifyCycleChanged()
    {
        OnPropertyChanged(nameof(ActualCycleSec));
        OnPropertyChanged(nameof(TargetCycleSec));
        OnPropertyChanged(nameof(RealCycleSec));
        OnPropertyChanged(nameof(CycleText));
        OnPropertyChanged(nameof(IsCycleSlow));
    }

    private (ShiftConfig? Shift, DateTime Start, DateTime End) ResolveCurrentShift()
        => ShiftConfigResolver.ResolveCurrentShift(_appSettings?.GetShiftsSnapshot(), _clock());

    /// <summary>班次已过时间变化后刷新达成进度（由产线页 1s 定时器调用）。</summary>
    public void RefreshShiftProgress()
    {
        OnPropertyChanged(nameof(ShiftTargetQuantity));
        OnPropertyChanged(nameof(ShiftExpectedQuantity));
        OnPropertyChanged(nameof(ShiftProgressRatio));
        OnPropertyChanged(nameof(ShiftProgressBarValue));
        OnPropertyChanged(nameof(ShiftProgressText));
        OnPropertyChanged(nameof(ShiftProgressFullText));
        OnPropertyChanged(nameof(HasShiftTarget));
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
