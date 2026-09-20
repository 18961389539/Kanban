using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Analysis;

/// <summary>
/// 复盘窗口计算（Collector 与 Web 回退路径单源）。健康分/结论文案仍由屏端本地化。
/// </summary>
public static class ReviewWindowMetrics
{
    private const double MinSegmentMinutes = 0.1;

    public static int CalculateProductionDeltaSorted(List<ProductionLogDto> logs, DateTime from, DateTime to)
    {
        if (logs.Count == 0) return 0;
        var lo = LowerBound(logs, from);
        var hi = UpperBound(logs, to);
        if (lo >= hi) return 0;

        var total = 0;
        var instanceStart = lo;
        for (var i = lo + 1; i <= hi; i++)
        {
            var boundary = i == hi;
            if (!boundary)
            {
                var prev = logs[i - 1];
                var cur = logs[i];
                boundary = cur.OkProduction < prev.OkProduction
                    || cur.NgProduction < prev.NgProduction
                    || cur.ShiftName != prev.ShiftName;
            }
            if (!boundary) continue;

            var first = logs[instanceStart];
            var last = logs[i - 1];
            ProductionLogDto baseline;
            if (first.Timestamp <= from)
            {
                baseline = first;
            }
            else
            {
                baseline = lo > 0 && logs[lo - 1].ShiftName == first.ShiftName ? logs[lo - 1] : first;
            }
            total += Math.Max(0, last.OkProduction - baseline.OkProduction);
            total += Math.Max(0, last.NgProduction - baseline.NgProduction);
            instanceStart = i;
        }
        return total;
    }

    public static List<ReviewAlarmItemDto> AnalyzeAlarms(
        List<AlarmEventRecordDto> events, List<ProductionLogDto> productionLogs, DateTime windowTo,
        DateTime? now = null)
    {
        var sortedLogs = productionLogs.OrderBy(log => log.Timestamp).ToList();
        var durationGroups = events
            .GroupBy(e => (e.AlarmName, e.DeviceName))
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.EventTime).ToList());

        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => new { e.AlarmName, e.DeviceName, e.PlcAddress })
            .Select(group =>
            {
                var triggers = group.OrderBy(e => e.EventTime).ToList();
                var intervals = triggers.Zip(triggers.Skip(1),
                    (first, second) => (second.EventTime - first.EventTime).TotalMinutes).ToList();
                var averageInterval = intervals.Count > 0 ? intervals.Average() : 0;
                return new ReviewAlarmItemDto
                {
                    AlarmName = group.Key.AlarmName,
                    DeviceName = group.Key.DeviceName,
                    PlcAddress = group.Key.PlcAddress,
                    TriggerCount = triggers.Count,
                    AverageIntervalMinutes = averageInterval,
                    IsHighFrequency = triggers.Count >= 3 || (triggers.Count >= 2 && averageInterval <= 30),
                    OutputBefore = triggers.Sum(trigger => CalculateProductionDeltaSorted(sortedLogs, trigger.EventTime.AddMinutes(-15), trigger.EventTime)),
                    OutputAfter = triggers.Sum(trigger => CalculateProductionDeltaSorted(sortedLogs, trigger.EventTime, trigger.EventTime.AddMinutes(15))),
                    ShiftName = triggers[0].ShiftName,
                    TotalDurationHours = CalculateAlarmDurationHours(
                        durationGroups.GetValueOrDefault((group.Key.AlarmName, group.Key.DeviceName)), windowTo, now),
                };
            })
            .OrderByDescending(item => item.TriggerCount)
            .ThenBy(item => item.AverageIntervalMinutes == 0 ? double.MaxValue : item.AverageIntervalMinutes)
            .Take(5)
            .ToList();
    }

    public static double CalculateAlarmDurationHours(List<AlarmEventRecordDto>? grouped, DateTime windowTo, DateTime? now = null)
    {
        if (grouped is null || grouped.Count == 0) return 0;
        var nowValue = now ?? DateTime.Now;
        var cutoff = windowTo < nowValue ? windowTo : nowValue;
        double seconds = 0;
        var recovers = grouped.Where(e => e.EventType == AlarmEventType.Recovered).ToList();
        var r = 0;

        foreach (var t in grouped.Where(e => e.EventType == AlarmEventType.Triggered))
        {
            while (r < recovers.Count && recovers[r].EventTime < t.EventTime)
                r++;
            if (r < recovers.Count)
            {
                seconds += Math.Max(0, (recovers[r].EventTime - t.EventTime).TotalSeconds);
                r++;
            }
            else
            {
                seconds += Math.Max(0, (cutoff - t.EventTime).TotalSeconds);
            }
        }
        return seconds / 3600.0;
    }

    public static (string Name, double Hours) FindLongestAlarm(List<AlarmEventRecordDto> events, DateTime windowTo, DateTime? now = null)
    {
        if (events.Count == 0) return (string.Empty, 0);
        var durations = events
            .GroupBy(e => e.AlarmName)
            .ToDictionary(g => g.Key, g => CalculateAlarmDurationHours(g.OrderBy(e => e.EventTime).ToList(), windowTo, now));
        if (durations.Count == 0) return (string.Empty, 0);
        var max = durations.Aggregate((a, b) => a.Value >= b.Value ? a : b);
        return (max.Key, max.Value);
    }

    public static List<ReviewTimelineSegmentDto> BuildTimeline(
        List<StatusTransitionRecordDto> transitions,
        List<AlarmEventRecordDto> alarms,
        List<ProductionLogDto> productionLogs,
        int initialState,
        DateTime from,
        DateTime to)
    {
        var ordered = transitions
            .Where(t => t.EventTime >= from && t.EventTime <= to)
            .OrderBy(t => t.EventTime)
            .ToList();
        var sortedLogs = productionLogs.OrderBy(log => log.Timestamp).ToList();
        var orderedAlarms = alarms
            .Where(a => a.EventType == AlarmEventType.Triggered)
            .OrderBy(a => a.EventTime)
            .ToList();

        var segments = new List<ReviewTimelineSegmentDto>();
        var cursor = from;
        var state = initialState;
        int alarmCursor = 0;
        foreach (var transition in ordered)
        {
            AppendOrMerge(segments, orderedAlarms, sortedLogs, state, cursor, transition.EventTime, ref alarmCursor);
            cursor = transition.EventTime;
            state = (int)transition.CurrentState;
        }
        if (to > cursor)
            AppendOrMerge(segments, orderedAlarms, sortedLogs, state, cursor, to, ref alarmCursor);
        return segments;
    }

    public static List<ReviewDefectRowDto> BuildDefectConcentrations(
        List<DefectSnapshotRecordDto> hourlyBounds, DateTime from, DateTime to)
    {
        var cells = new List<(string Name, string Shift, DateTime Bucket, int Count)>();
        foreach (var group in hourlyBounds.GroupBy(s => new { s.DefectId, s.ShiftName }))
        {
            var previous = group.OrderBy(s => s.Timestamp)
                .LastOrDefault(s => s.Timestamp < from);
            foreach (var snapshot in group.Where(s => s.Timestamp >= from && s.Timestamp <= to)
                .OrderBy(s => s.Timestamp))
            {
                var count = previous == null
                    ? Math.Max(0, snapshot.Count)
                    : Math.Max(0, snapshot.Count - previous.Count);
                if (count > 0)
                {
                    var bucket = new DateTime(snapshot.Timestamp.Year, snapshot.Timestamp.Month,
                        snapshot.Timestamp.Day, snapshot.Timestamp.Hour, 0, 0);
                    cells.Add((snapshot.DefectName, snapshot.ShiftName, bucket, count));
                }
                previous = snapshot;
            }
        }

        var total = cells.Sum(cell => cell.Count);
        return cells
            .GroupBy(cell => new { cell.Name, cell.Shift, cell.Bucket })
            .Select(group => new ReviewDefectRowDto
            {
                DefectName = group.Key.Name,
                ShiftName = group.Key.Shift,
                TimeRangeText = group.Key.Bucket.ToString("MM-dd HH:00"),
                Count = group.Sum(cell => cell.Count),
                Share = total > 0 ? (double)group.Sum(cell => cell.Count) / total : 0,
            })
            .OrderByDescending(item => item.Count)
            .Take(10)
            .ToList();
    }

    public static (string PeakHour, int PeakOk, string ValleyHour, int ValleyOk) FindPeakValley(
        List<ProductionLogDto> logs, DateTime from, DateTime to)
    {
        var sorted = logs.OrderBy(log => log.Timestamp).ToList();
        var hours = new List<(string Label, int Ok)>();
        var cursor = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0);
        var end = to;
        while (cursor <= end)
        {
            var hourEnd = cursor.AddHours(1);
            hours.Add((cursor.ToString("HH:mm"),
                CalculateProductionDeltaSorted(sorted, cursor, hourEnd < end ? hourEnd : end)));
            cursor = hourEnd;
        }
        if (hours.Count == 0) return ("—", 0, "—", 0);

        var peak = hours.MaxBy(h => h.Ok);
        var valley = hours.MinBy(h => h.Ok);
        return (peak.Label, peak.Ok, valley.Label, valley.Ok);
    }

    public static List<ReviewShiftRowDto> BuildShiftSummaries(
        List<ProductionLogDto> logs, List<AlarmEventRecordDto> alarms)
    {
        var result = new List<ReviewShiftRowDto>();
        foreach (var group in ProductionWindowMetrics.SplitShiftInstances(logs))
        {
            var first = group.First();
            var shiftName = first.ShiftName;
            var ok = Math.Max(0, group.Last().OkProduction - first.OkProduction);
            var ng = Math.Max(0, group.Last().NgProduction - first.NgProduction);
            var alarmCount = alarms.Count(a => a.ShiftName == shiftName && a.EventType == AlarmEventType.Triggered);
            result.Add(new ReviewShiftRowDto
            {
                ShiftName = shiftName,
                OkCount = ok,
                NgCount = ng,
                AlarmCount = alarmCount,
            });
        }
        return result;
    }

    private static void AppendOrMerge(
        List<ReviewTimelineSegmentDto> segments,
        List<AlarmEventRecordDto> orderedAlarms,
        List<ProductionLogDto> sortedLogs,
        int state,
        DateTime start,
        DateTime end,
        ref int alarmCursor)
    {
        if ((end - start).TotalMinutes < MinSegmentMinutes)
        {
            if (segments.Count > 0)
            {
                var prev = segments[^1];
                segments[^1] = CreateSegment(prev.StatusWord, prev.Start, end, sortedLogs, orderedAlarms, ref alarmCursor);
            }
            return;
        }
        segments.Add(CreateSegment(state, start, end, sortedLogs, orderedAlarms, ref alarmCursor));
    }

    private static ReviewTimelineSegmentDto CreateSegment(
        int state,
        DateTime start,
        DateTime end,
        List<ProductionLogDto> sortedLogs,
        List<AlarmEventRecordDto> orderedAlarms,
        ref int alarmCursor)
    {
        var output = CalculateProductionDeltaSorted(sortedLogs, start, end);
        var alarmCount = 0;
        while (alarmCursor < orderedAlarms.Count && orderedAlarms[alarmCursor].EventTime < end)
        {
            if (orderedAlarms[alarmCursor].EventTime >= start)
                alarmCount++;
            alarmCursor++;
        }
        return new ReviewTimelineSegmentDto
        {
            Start = start,
            End = end,
            StatusWord = state,
            OutputDelta = output,
            AlarmCount = alarmCount,
            HasNoOutput = state == (int)DeviceStatus.Running && output == 0,
        };
    }

    private static int LowerBound(List<ProductionLogDto> logs, DateTime t)
    {
        int lo = 0, hi = logs.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (logs[mid].Timestamp < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(List<ProductionLogDto> logs, DateTime t)
    {
        int lo = 0, hi = logs.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (logs[mid].Timestamp <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
