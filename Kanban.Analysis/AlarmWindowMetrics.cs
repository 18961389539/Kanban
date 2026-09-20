using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Analysis;

/// <summary>
/// 报警窗口 KPI / Top 排行（Collector 与 Web 回退路径单源）。
/// 配对口径对齐 Web AlarmAnalysis.BuildTopStats：每条 Recovered 只消费一次。
/// </summary>
public static class AlarmWindowMetrics
{
    public const int DefaultTopCount = 10;
    public const int DefaultRecentCount = 30;

    public static AlarmWindowStatsDto Build(
        IReadOnlyList<AlarmEventRecordDto> events,
        DateTime windowFrom,
        DateTime windowTo,
        bool truncated)
    {
        var todayStart = windowTo.Date;
        var yesterdayStart = todayStart.AddDays(-1);
        var window = events.Where(e => e.EventTime >= windowFrom && e.EventTime <= windowTo).ToList();
        var today = events.Where(e => e.EventTime >= todayStart && e.EventTime <= windowTo).ToList();
        var yesterday = events.Where(e => e.EventTime >= yesterdayStart && e.EventTime < todayStart).ToList();

        var (corrPattern, corrCount) = BuildCorrelation(window);
        return new AlarmWindowStatsDto
        {
            Truncated = truncated,
            WindowTriggered = CountTriggered(window),
            WindowRecovered = CountRecovered(window),
            TodayTriggered = CountTriggered(today),
            TodayRecovered = CountRecovered(today),
            YesterdayTriggered = CountTriggered(yesterday),
            Top = BuildTop(window, DefaultTopCount),
            Recent = window.OrderByDescending(e => e.EventTime).Take(DefaultRecentCount).ToList(),
            Pending = CountPending(window),
            Chart = BuildChart(window),
            AlarmNames = window
                .Select(e => e.AlarmName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList(),
            ShiftNames = window
                .Select(e => e.ShiftName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList(),
            CorrelationPattern = corrPattern,
            CorrelationCount = corrCount,
            PendingTop = BuildPendingTop(window, windowTo),
        };
    }

    public static int CountPending(IReadOnlyList<AlarmEventRecordDto> events)
        => events
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .Count(g => g.OrderByDescending(e => e.EventTime).First().EventType == AlarmEventType.Triggered);

    public static List<AlarmChartStatDto> BuildChart(IReadOnlyList<AlarmEventRecordDto> events)
    {
        var recovers = events
            .Where(e => e.EventType == AlarmEventType.Recovered)
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .ToDictionary(k => k.Key, v => new Queue<AlarmEventRecordDto>(v.OrderBy(e => e.EventTime)));

        var acc = new Dictionary<string, (int TriggerCount, double TotalMinutes, int PairedCount)>();
        foreach (var t in events
                     .Where(e => e.EventType == AlarmEventType.Triggered)
                     .OrderBy(e => e.EventTime))
        {
            if (!acc.TryGetValue(t.AlarmName, out var item))
                item = (0, 0, 0);
            item.TriggerCount++;
            var key = new { t.DeviceId, t.AlarmId };
            if (recovers.TryGetValue(key, out var queue))
            {
                while (queue.Count > 0 && queue.Peek().EventTime <= t.EventTime)
                    queue.Dequeue();
                if (queue.Count > 0)
                {
                    var recovery = queue.Dequeue();
                    item.TotalMinutes += (recovery.EventTime - t.EventTime).TotalMinutes;
                    item.PairedCount++;
                }
            }
            acc[t.AlarmName] = item;
        }

        return acc
            .Select(kv => new AlarmChartStatDto
            {
                AlarmName = kv.Key,
                TriggerCount = kv.Value.TriggerCount,
                AvgDurationMin = kv.Value.PairedCount > 0
                    ? kv.Value.TotalMinutes / kv.Value.PairedCount
                    : 0,
            })
            .OrderByDescending(x => x.TriggerCount)
            .ToList();
    }

    private static (string? Pattern, int Count) BuildCorrelation(IReadOnlyList<AlarmEventRecordDto> events)
    {
        var window = TimeSpan.FromMinutes(5);
        var triggers = events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .OrderBy(e => e.EventTime)
            .ToList();
        if (triggers.Count < 2) return (null, 0);

        List<List<string>> chains = [];
        for (int i = 0; i < triggers.Count; i++)
        {
            var seed = triggers[i];
            var seen = new HashSet<string> { seed.AlarmName ?? "?" };
            List<string> chain = [seed.AlarmName ?? "?"];
            for (int j = i + 1; j < triggers.Count; j++)
            {
                if (triggers[j].EventTime - seed.EventTime > window) break;
                var name = triggers[j].AlarmName ?? "?";
                if (seen.Add(name)) chain.Add(name);
            }
            if (chain.Count >= 2) chains.Add(chain);
        }
        if (chains.Count == 0) return (null, 0);

        var top = chains
            .GroupBy(c => string.Join("→", c))
            .Select(g => (Pattern: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 2)
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();
        return top.Pattern is null ? (null, 0) : (top.Pattern, top.Count);
    }

    private static List<AlarmPendingItemDto> BuildPendingTop(IReadOnlyList<AlarmEventRecordDto> events, DateTime effectiveTo)
    {
        var triggers = events.Where(e => e.EventType == AlarmEventType.Triggered).ToList();
        var recovers = events.Where(e => e.EventType == AlarmEventType.Recovered).ToList();
        List<AlarmPendingItemDto> pending = [];
        foreach (var g in triggers.GroupBy(e => new { e.DeviceId, e.AlarmId, e.AlarmName }))
        {
            var lastTrigger = g.OrderByDescending(e => e.EventTime).First();
            var hasRecoverAfter = recovers.Any(r =>
                r.DeviceId == g.Key.DeviceId
                && r.AlarmId == g.Key.AlarmId
                && r.EventTime > lastTrigger.EventTime);
            if (hasRecoverAfter) continue;
            var minutes = (effectiveTo - lastTrigger.EventTime).TotalMinutes;
            if (minutes > 0)
                pending.Add(new AlarmPendingItemDto
                {
                    AlarmName = g.Key.AlarmName ?? "?",
                    TriggerTime = lastTrigger.EventTime,
                    Minutes = minutes,
                });
        }
        return pending.OrderByDescending(x => x.Minutes).Take(3).ToList();
    }

    public static List<AlarmTopRowDto> BuildTop(IReadOnlyList<AlarmEventRecordDto> events, int take = DefaultTopCount)
    {
        var recovers = events
            .Where(e => e.EventType == AlarmEventType.Recovered)
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .ToDictionary(k => k.Key, v => new Queue<AlarmEventRecordDto>(v.OrderBy(e => e.EventTime)));

        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => new { e.AlarmName, e.DeviceName })
            .Select(g =>
            {
                double totalMinutes = 0;
                foreach (var t in g.OrderBy(e => e.EventTime))
                {
                    var key = new { t.DeviceId, t.AlarmId };
                    if (!recovers.TryGetValue(key, out var recQueue)) continue;
                    while (recQueue.Count > 0 && recQueue.Peek().EventTime <= t.EventTime)
                        recQueue.Dequeue();
                    if (recQueue.Count == 0) continue;
                    var recovery = recQueue.Dequeue();
                    totalMinutes += (recovery.EventTime - t.EventTime).TotalMinutes;
                }
                return new AlarmTopRowDto
                {
                    AlarmName = g.Key.AlarmName,
                    DeviceName = g.Key.DeviceName,
                    TriggerCount = g.Count(),
                    TotalDurationMinutes = totalMinutes,
                };
            })
            .OrderByDescending(x => x.TriggerCount)
            .Take(take)
            .ToList();
    }

    private static int CountTriggered(IEnumerable<AlarmEventRecordDto> events)
        => events.Count(e => e.EventType == AlarmEventType.Triggered);

    private static int CountRecovered(IEnumerable<AlarmEventRecordDto> events)
        => events.Count(e => e.EventType == AlarmEventType.Recovered);
}
