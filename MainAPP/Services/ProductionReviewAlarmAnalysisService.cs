using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Entities;

namespace MainAPP.Services;

public interface IProductionReviewAlarmAnalysisService
{
    IReadOnlyList<ReviewAlarmAnalysisData> Analyze(
        IReadOnlyList<AlarmEventRecord> events,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime windowTo,
        string? deviceId = null);
}

public sealed class ProductionReviewAlarmAnalysisService : IProductionReviewAlarmAnalysisService
{
    /// <summary>
    /// 汇总窗口内报警（按 报警名+设备+地址 分组取 Top 5）。
    /// deviceId 非空时强制按设备过滤：复盘页为单设备视角，必须防止全厂报警串入所选设备。
    /// </summary>
    public IReadOnlyList<ReviewAlarmAnalysisData> Analyze(
        IReadOnlyList<AlarmEventRecord> events,
        IReadOnlyList<ProductionLog> productionLogs,
        DateTime windowTo,
        string? deviceId = null)
    {
        // 预排序一次：产量差分二分定位（2026-08-11 性能修复，原每 trigger 全量扫描 1.3 万条 → 9.2s）
        var sortedLogs = productionLogs.OrderBy(log => log.Timestamp).ToList();
        // 报警时长：按 (报警名, 设备) 预分组一次，避免每组对全量事件重复过滤+排序
        var durationGroups = events
            .GroupBy(e => (e.AlarmName, e.DeviceName))
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.EventTime).ToList());

        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .Where(e => deviceId == null || e.DeviceId == deviceId)
            .GroupBy(e => new { e.AlarmName, e.DeviceName, e.PlcAddress })
            .Select(group =>
            {
                var triggers = group.OrderBy(e => e.EventTime).ToList();
                var intervals = triggers.Zip(triggers.Skip(1),
                    (first, second) => (second.EventTime - first.EventTime).TotalMinutes).ToList();
                var averageInterval = intervals.Count > 0 ? intervals.Average() : 0;
                return new ReviewAlarmAnalysisData(
                    group.Key.AlarmName,
                    group.Key.DeviceName,
                    group.Key.PlcAddress,
                    triggers.Count,
                    averageInterval,
                    triggers.Count >= 3 || (triggers.Count >= 2 && averageInterval <= 30),
                    triggers.Sum(trigger => ProductionReviewCalculations.CalculateProductionDeltaSorted(
                        sortedLogs, trigger.EventTime.AddMinutes(-15), trigger.EventTime)),
                    triggers.Sum(trigger => ProductionReviewCalculations.CalculateProductionDeltaSorted(
                        sortedLogs, trigger.EventTime, trigger.EventTime.AddMinutes(15))),
                    triggers[0].ShiftName,
                    CalculateAlarmDurationHours(
                        durationGroups.GetValueOrDefault((group.Key.AlarmName, group.Key.DeviceName)),
                        windowTo));
            })
            .OrderByDescending(item => item.TriggerCount)
            .ThenBy(item => item.AverageIntervalMinutes == 0 ? double.MaxValue : item.AverageIntervalMinutes)
            .Take(5)
            .ToList();
    }

    /// <summary>
    /// 计算报警触发→恢复的配对时长。P2-13 口径修复：
    /// ① 未恢复的 Triggered 用 min(now, windowTo) 截断——历史窗口内未恢复报警不再算到"现在"（可超出窗口数倍）；
    /// ② 每个 Recovered 只消费一次——连续 Triggered（T1,T2,R1）时 T1 配 R1、T2 未恢复按截断值，避免重复配对高估时长。
    /// </summary>
    private static double CalculateAlarmDurationHours(
        List<AlarmEventRecord>? grouped,
        DateTime windowTo)
    {
        if (grouped is null || grouped.Count == 0) return 0;
        double seconds = 0;
        var end = windowTo < DateTime.Now ? windowTo : DateTime.Now;
        // 队列 FIFO 单趟扫描（审查修复 2026-08-13）：原对每个 Triggered 向后线性扫未消费的 Recovered，
        // 高频报警组（数万条且恢复稀疏）退化为 O(n²)——此处 O(n)。
        // 语义与原实现完全一致：每条 Recovered 只消费**最早**一条未配对 Triggered（FIFO）；
        // 末尾未配对的 Triggered 按截断值计。
        var pending = new Queue<AlarmEventRecord>();
        foreach (var e in grouped) // 调用方已按 EventTime 排序（AnalyzeAlarms 内 OrderBy）
        {
            if (e.EventType == AlarmEventType.Triggered)
            {
                pending.Enqueue(e);
            }
            else if (e.EventType == AlarmEventType.Recovered && pending.Count > 0)
            {
                var t = pending.Dequeue();
                seconds += Math.Max(0, (e.EventTime - t.EventTime).TotalSeconds);
            }
        }
        while (pending.Count > 0)
        {
            var t = pending.Dequeue();
            seconds += Math.Max(0, (end - t.EventTime).TotalSeconds);
        }
        return seconds / 3600.0;
    }
}
