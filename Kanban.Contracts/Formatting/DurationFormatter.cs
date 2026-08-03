namespace Kanban.Contracts.Formatting;

/// <summary>
/// 时长格式化跨进程单源实现（WPF / Kanban.Collector / WASM 屏端共用）。
/// 历史问题：同一"时长→文本"逻辑曾在三端复制 6 份且口径分叉（有的含秒、有的不含），
/// 一处调整需同步多处、极易漏改。现收敛为本类唯一实现，各端委托调用。
/// </summary>
public static class DurationFormatter
{
    /// <summary>
    /// 紧凑时长（班次/工单场景）：≥1h → "Xh Ym"，否则 → "Xm"。
    /// 对齐 HomeViewModel/ShiftProgressProvider 的 FormatShiftTime 口径。
    /// </summary>
    public static string FormatCompact(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : $"{ts.Minutes}m";
    }

    /// <summary>
    /// 标准时长（状态卡场景）：≥1h → "Xh Ym"，≥1m → "Xm Ys"，否则 → "Xs"。
    /// 对齐 WPF FormatHelper.FormatDuration 口径；负值按 0 处理。
    /// </summary>
    public static string FormatStandard(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : ts.TotalMinutes >= 1
                ? $"{ts.Minutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";
    }
}
