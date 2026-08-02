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
    public static string FormatDuration(double secs)
    {
        var ts = TimeSpan.FromSeconds(secs);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
            : ts.TotalMinutes >= 1
                ? $"{ts.Minutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";
    }
}