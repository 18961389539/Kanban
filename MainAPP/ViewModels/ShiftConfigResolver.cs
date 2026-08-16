using Kanban.Collector.Core.Models;

namespace MainAPP.ViewModels;

/// <summary>
/// 班次解析共享助手：按完整 DateTime（含日期，支持跨午夜班次）解析当前时刻所属班次及其起止时间。
/// 供主页班次进度与上班次对比复用，消除重复的 ResolveRange 遍历。
/// </summary>
public static class ShiftConfigResolver
{
    public static (ShiftConfig? Shift, DateTime Start, DateTime End) ResolveCurrentShift(
        IReadOnlyList<ShiftConfig>? shifts,
        DateTime now)
    {
        if (shifts == null || shifts.Count == 0) return (null, default, default);
        foreach (var sc in shifts)
        {
            var (start, end) = sc.ResolveRange(now);
            if (start <= now && now < end) return (sc, start, end);
        }
        return (null, default, default);
    }
}
