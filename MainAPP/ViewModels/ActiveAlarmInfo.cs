using CommunityToolkit.Mvvm.ComponentModel;
using Kanban.Collector.Core.Models;

namespace MainAPP.ViewModels;

/// <summary>
/// 实时故障列表项（用于 HomeView / AlarmCenterView 的活跃报警绑定）。
/// Duration 由 SyncRuntime 每 3 秒更新一次（基于 EventTime 差值）。
/// </summary>
public partial class ActiveAlarmInfo : ObservableObject
{
    public DateTime EventTime { get; }
    public string DeviceName { get; }
    public string AlarmName { get; }
    public AlarmLevel Level { get; }
    public AlarmKind Kind { get; }

    [ObservableProperty] private string _durationText = "";
    /// <summary>
    /// 是否为新加入报警（用于 UI 高亮闪烁，30 秒后由 SyncRuntime 清除）。
    /// 默认 false，仅 RefreshActiveAlarms 中新加入列表时设为 true。
    /// </summary>
    [ObservableProperty] private bool _isNew = false;

    /// <summary>加入列表的时刻，用于清除 IsNew 标志。重新触发时重置。</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public ActiveAlarmInfo(DateTime eventTime, string deviceName, string alarmName, AlarmLevel level, AlarmKind kind)
    {
        EventTime = eventTime;
        DeviceName = deviceName;
        AlarmName = alarmName;
        Level = level;
        Kind = kind;
    }

    /// <summary>
    /// 基于传入时间刷新持续时间文本（HH:mm:ss 格式，超过 1 小时显示 Hh Mm Ss）。
    /// </summary>
    public void RefreshDuration(DateTime now)
    {
        var ts = now - EventTime;
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        DurationText = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"
            : $"{ts.Minutes}m {ts.Seconds}s";
    }

    /// <summary>
    /// 同值判定（不含 DurationText，用于差分更新比较）。
    /// </summary>
    public bool Equals(ActiveAlarmInfo? other) =>
        other != null && EventTime == other.EventTime
        && DeviceName == other.DeviceName && AlarmName == other.AlarmName
        && Level == other.Level && Kind == other.Kind;

    public override bool Equals(object? obj) => obj is ActiveAlarmInfo other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(EventTime, DeviceName, AlarmName, Level, Kind);
}

/// <summary>
/// 报警类型区分：PLC 边沿触发 vs 计数阈值触发。
/// </summary>
public enum AlarmKind
{
    /// <summary>PLC M 位边沿触发报警</summary>
    Plc,

    /// <summary>数值累计超阈值报警</summary>
    Count
}
