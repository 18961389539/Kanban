using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 从历史事件中提取未恢复的数据源报警（AlarmId 以 src: 前缀，末条事件为 Triggered）。
/// 活跃墙 3s 刷新用较短回溯窗口；统计刷新用较长窗口。
/// </summary>
public static class PendingDataSourceAlarmQuery
{
    public static readonly TimeSpan ActiveLookback = TimeSpan.FromDays(7);
    public static readonly TimeSpan StatsLookback = TimeSpan.FromDays(30);

    public static List<AlarmEventRecord> QueryPending(
        IAlarmHistoryService historyService,
        DateTime now,
        string? deviceId,
        TimeSpan lookback)
    {
        var from = now - lookback;
        var allEvents = historyService.QueryAlarmEventsStrict(from, now, deviceId);
        return ExtractPending(allEvents);
    }

    /// <summary>活跃刷新用：查询失败时返回 null，调用方回退缓存。</summary>
    public static List<AlarmEventRecord>? TryQueryPending(
        IAlarmHistoryService historyService,
        DateTime now,
        string? deviceId,
        TimeSpan lookback)
    {
        try
        {
            return QueryPending(historyService, now, deviceId, lookback);
        }
        catch
        {
            return null;
        }
    }

    public static List<AlarmEventRecord> ExtractPending(IEnumerable<AlarmEventRecord> allEvents) =>
        allEvents
            .Where(e => e.AlarmId.StartsWith("src:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.AlarmId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.EventTime).First())
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .ToList();
}
