using System;

namespace MainAPP.Helpers;

/// <summary>
/// 通用格式化工具：DiffText（▲/▼ 差异文本）和 FormatDuration（时长格式化）。
/// 消除 HomeViewModel / ProductionLineViewModel / LineDeviceItem 中的重复格式化逻辑。
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// 将整数差异格式化为带箭头的显示文本：正数前缀 "▲"，负数前缀 "▼"，0 返回空字符串。
    /// 用于 UI 在差异为 0 时隐藏徽章。
    /// </summary>
    public static string FormatDiff(int diff) => diff switch
    {
        > 0 => $"▲{diff}",
        < 0 => $"▼{Math.Abs(diff)}",
        _ => ""
    };

    /// <summary>
    /// 将秒数格式化为人类可读的时长文本：
    /// ≥1h → "Xh Ym"，≥1m → "Xm Ys"，否则 → "Xs"。
    /// </summary>
    /// <remarks>口径委托 Kanban.Contracts.DurationFormatter.FormatStandard（跨进程单源，勿在此内联）。</remarks>
    public static string FormatDuration(double secs)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatStandard(secs);

    /// <summary>
    /// 完整时长（始终含时分秒）：设备状态卡等需要秒级精度的场景。
    /// </summary>
    public static string FormatDurationFull(double secs)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatFull(secs);

    /// <summary>时刻显示：始终 24 小时制 HH:mm（00–23）。勿对 DateTime 使用 hh（12 小时且无 AM/PM）。</summary>
    public static string FormatClock(DateTime time) => time.ToString("HH:mm");

    /// <summary>班次配置时刻：TimeSpan.Hours 为 0–23，按 HH:mm 输出。勿用 DateTime 的 hh 自定义格式。</summary>
    public static string FormatClock(TimeSpan time) => $"{time.Hours:D2}:{time.Minutes:D2}";
}