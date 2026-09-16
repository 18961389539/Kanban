using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Web.Services;

/// <summary>
/// 生产复盘分析（Web 端）：从 WPF ProductionReview* 服务族移植，操作对象改为跨进程 DTO。
/// 覆盖：窗口产量差分（二分定位）、报警分析（频次/间隔/前后产量/时长配对）、状态时间线
/// （最短段合并）、缺陷集中度（小时分组下推）、健康评分（结构化扣分）、峰值/谷值时段、
/// 班次汇总、结构化复盘结论。阈值与 WPF 逐条对齐。
/// </summary>
public static class ReviewAnalysis
{
    public const double QualityTarget = 0.95;
    public const double OeeTarget = 0.85;

    // ──────────── 窗口产量差分（二分定位，O(log n + m)） ────────────

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

    /// <summary>有序日志上的窗口差分（logs 按 Timestamp 升序）：按班次实例切分，每实例 末值 - 基线。与 WPF 逐条一致。</summary>
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
            ProductionLogDto? baseline;
            if (first.Timestamp <= from)
            {
                baseline = first;
            }
            else
            {
                baseline = lo > 0 && logs[lo - 1].ShiftName == first.ShiftName ? logs[lo - 1] : null;
                baseline ??= first;
            }
            total += Math.Max(0, last.OkProduction - baseline.OkProduction);
            total += Math.Max(0, last.NgProduction - baseline.NgProduction);
            instanceStart = i;
        }
        return total;
    }

    // ──────────── 报警分析（Top 5：频次/平均间隔/前后 15 分钟产量/时长配对） ────────────

    public sealed record ReviewAlarmItem(
        string AlarmName, string DeviceName, string PlcAddress,
        int TriggerCount, double AverageIntervalMinutes, bool IsHighFrequency,
        int OutputBefore, int OutputAfter, string ShiftName, double TotalDurationHours);

    /// <param name="now">未恢复报警截断用的"当前时刻"——浏览器时区与工厂不同时须传 Dashboard.ServerNow（默认回退本机时间）。</param>
    public static List<ReviewAlarmItem> AnalyzeAlarms(
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
                return new ReviewAlarmItem(
                    group.Key.AlarmName,
                    group.Key.DeviceName,
                    group.Key.PlcAddress,
                    triggers.Count,
                    averageInterval,
                    triggers.Count >= 3 || (triggers.Count >= 2 && averageInterval <= 30),
                    triggers.Sum(trigger => CalculateProductionDeltaSorted(sortedLogs, trigger.EventTime.AddMinutes(-15), trigger.EventTime)),
                    triggers.Sum(trigger => CalculateProductionDeltaSorted(sortedLogs, trigger.EventTime, trigger.EventTime.AddMinutes(15))),
                    triggers[0].ShiftName,
                    CalculateAlarmDurationHours(durationGroups.GetValueOrDefault((group.Key.AlarmName, group.Key.DeviceName)), windowTo, now));
            })
            .OrderByDescending(item => item.TriggerCount)
            .ThenBy(item => item.AverageIntervalMinutes == 0 ? double.MaxValue : item.AverageIntervalMinutes)
            .Take(5)
            .ToList();
    }

    /// <summary>报警触发→恢复配对时长：未恢复按 min(now, windowTo) 截断；每个 Recovered 只消费一次。与 WPF 一致。
    /// O(n)：用 Recovered 游标顺序消费替代原「每 Triggered 内层前扫 + HashSet」，消除 O(n²)。</summary>
    private static double CalculateAlarmDurationHours(List<AlarmEventRecordDto>? grouped, DateTime windowTo, DateTime? now = null)
    {
        if (grouped is null || grouped.Count == 0) return 0;
        var nowValue = now ?? DateTime.Now;
        var cutoff = windowTo < nowValue ? windowTo : nowValue;
        double seconds = 0;

        // Recovered 按时间升序，游标只前进——等价于原「consumed HashSet + 内层 for」，但 O(n)
        var recovers = grouped.Where(e => e.EventType == AlarmEventType.Recovered).ToList();
        var r = 0;

        foreach (var t in grouped.Where(e => e.EventType == AlarmEventType.Triggered))
        {
            while (r < recovers.Count && recovers[r].EventTime < t.EventTime)
                r++;
            if (r < recovers.Count)
            {
                seconds += Math.Max(0, (recovers[r].EventTime - t.EventTime).TotalSeconds);
                r++; // 消费该 Recovered
            }
            else
            {
                seconds += Math.Max(0, (cutoff - t.EventTime).TotalSeconds);
            }
        }
        return seconds / 3600.0;
    }

    /// <summary>持续时间最长的报警（按 AlarmName 配对总时长最大者），返回值单位为**小时**。与 WPF FindLongestAlarm 一致。
    /// now：未恢复报警截断用的"当前时刻"——浏览器时区与工厂不同时须传 Dashboard.ServerNow（默认回退本机时间）。</summary>
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

    // ──────────── 状态时间线（最短段合并，连续无空洞） ────────────

    public sealed record ReviewSegment(DateTime Start, DateTime End, int StatusWord, int OutputDelta, int AlarmCount, bool HasNoOutput);

    /// <summary>状态段最短时长：低于该值的抖动状态并入前一段。与 WPF MinSegmentMinutes 一致。</summary>
    private const double MinSegmentMinutes = 0.1;

    public static List<ReviewSegment> BuildTimeline(
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

        var segments = new List<ReviewSegment>();
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

    private static void AppendOrMerge(
        List<ReviewSegment> segments,
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

    private static ReviewSegment CreateSegment(
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
        return new ReviewSegment(start, end, state, output, alarmCount,
            state == (int)DeviceStatus.Running && output == 0);
    }

    // ──────────── 缺陷集中度（小时分组下推数据 → 逐时差分） ────────────

    public sealed record ReviewDefectConcentration(string DefectName, string ShiftName, string TimeRangeText, int Count, double Share);

    public static List<ReviewDefectConcentration> BuildDefectConcentrations(
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
            .Select(group => new ReviewDefectConcentration(
                group.Key.Name,
                group.Key.Shift,
                group.Key.Bucket.ToString("MM-dd HH:00"),
                group.Sum(cell => cell.Count),
                total > 0 ? (double)group.Sum(cell => cell.Count) / total : 0))
            .OrderByDescending(item => item.Count)
            .Take(10)
            .ToList();
    }

    // ──────────── 健康评分（结构化扣分：低产 30 / 缺陷突增 25 / 报警突增 20 / 运行无产量 30） ────────────

    public static (List<string> Issues, int Score) CalculateHealth(
        int targetCycle,
        List<ProductionLogDto> productionLogs,
        List<AlarmEventRecordDto> alarms,
        List<ReviewSegment> timeline,
        List<ReviewDefectConcentration> concentrations,
        int previousAlarmCount,
        int previousDefectCount,
        DateTime from,
        DateTime to,
        Func<string, object[], string> L)
    {
        List<(string Text, int Deduct)> issues = [];
        var totalOutput = CalculateProductionDeltaSorted(productionLogs.OrderBy(log => log.Timestamp).ToList(), from, to);
        var runHours = timeline.Where(s => s.StatusWord == (int)DeviceStatus.Running)
            .Sum(s => (s.End - s.Start).TotalMinutes) / 60.0;
        if (targetCycle > 0 && runHours > 0 && totalOutput / runHours < targetCycle * 0.8)
            issues.Add((L("Rv_HealthLowOutput", [totalOutput / runHours, targetCycle]), 30));

        var currentAlarmCount = alarms.Count(a => a.EventType == AlarmEventType.Triggered);
        if (currentAlarmCount >= 3 && (previousAlarmCount == 0 || currentAlarmCount > previousAlarmCount * 1.5))
            issues.Add((L("Rv_HealthAlarmSpike", [currentAlarmCount, previousAlarmCount]), 20));

        var defectCount = concentrations.Sum(item => item.Count);
        if (defectCount >= 3 && (previousDefectCount == 0 || defectCount > previousDefectCount * 1.5))
            issues.Add((L("Rv_HealthDefectSpike", [defectCount, previousDefectCount]), 25));

        var idleRunning = timeline.FirstOrDefault(s => s.HasNoOutput && (s.End - s.Start).TotalMinutes >= 30);
        if (idleRunning != null)
            issues.Add((L("Rv_HealthNoOutput", [idleRunning.Start.ToString("HH:mm:ss"), idleRunning.End.ToString("HH:mm:ss"), (idleRunning.End - idleRunning.Start).TotalMinutes]), 30));

        var score = 100;
        foreach (var issue in issues)
            score -= issue.Deduct;
        return (issues.Select(i => i.Text).ToList(), Math.Clamp(score, 0, 100));
    }

    // ──────────── 峰值/谷值时段（小时桶 OK 差分） ────────────

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

    // ──────────── 班次汇总 ────────────

    public sealed record ReviewShiftSummary(string ShiftName, int OkCount, int NgCount, int AlarmCount);

    public static List<ReviewShiftSummary> BuildShiftSummaries(
        List<ProductionLogDto> logs, List<AlarmEventRecordDto> alarms)
    {
        var result = new List<ReviewShiftSummary>();
        foreach (var group in ProductionAnalysis.SplitShiftInstances(logs))
        {
            var first = group.First();
            var shiftName = first.ShiftName;
            var ok = Math.Max(0, group.Last().OkProduction - first.OkProduction);
            var ng = Math.Max(0, group.Last().NgProduction - first.NgProduction);
            var alarmCount = alarms.Count(a => a.ShiftName == shiftName && a.EventType == AlarmEventType.Triggered);
            result.Add(new ReviewShiftSummary(shiftName, ok, ng, alarmCount));
        }
        return result;
    }

    // ──────────── 结构化复盘结论（目标：良品率 95% / OEE 85%） ────────────

    public sealed record ReviewConclusion(string Text, string Kind, bool? Met);

    public static List<ReviewConclusion> BuildConclusions(
        int totalOk,
        int totalNg,
        int totalAlarmCount,
        double longestDowntimeHours,
        string longestDowntimeDevice,
        string longestDowntimeAlarm,
        List<ReviewShiftSummary> shifts,
        List<ReviewDefectConcentration> defects,
        double quality,
        double oee,
        Func<string, object[], string> L)
    {
        List<ReviewConclusion> result = [];
        var total = totalOk + totalNg;
        if (total == 0 && totalAlarmCount == 0)
            return [new ReviewConclusion(L("Rv_NoConclusion", []), "info", null)];

        if (total > 0)
        {
            var met = quality >= QualityTarget;
            result.Add(new ReviewConclusion(
                L("Rv_QualityConclusion", [quality, met ? L("Rv_Met", []) : L("Rv_NotMet", []), QualityTarget]),
                "quality", met));
        }

        if (oee > 0)
        {
            var met = oee >= OeeTarget;
            result.Add(new ReviewConclusion(
                L("Rv_OeeConclusion", [oee, met ? L("Rv_Met", []) : L("Rv_NotMet", []), OeeTarget]),
                "oee", met));
        }

        if (longestDowntimeHours > 0)
        {
            result.Add(new ReviewConclusion(
                // 审查修复 2026-08-13：FindLongestAlarm 返回的已是小时，此前再除 3600 导致结论恒 ≈0
                L("Rv_DowntimeConclusion", [longestDowntimeHours, longestDowntimeDevice, longestDowntimeAlarm]),
                "downtime", null));
        }

        var topShift = shifts.Where(s => s.OkCount + s.NgCount > 0).OrderByDescending(s => s.OkCount + s.NgCount).FirstOrDefault();
        if (topShift != null)
        {
            var okRatio = (topShift.OkCount + topShift.NgCount) > 0 ? (double)topShift.OkCount / (topShift.OkCount + topShift.NgCount) : 0;
            result.Add(new ReviewConclusion(
                L("Rv_BestShiftConclusion", [topShift.ShiftName, topShift.OkCount + topShift.NgCount, okRatio]),
                "bestshift", null));
        }

        var topDefect = defects.FirstOrDefault();
        if (topDefect != null)
        {
            result.Add(new ReviewConclusion(
                L("Rv_TopDefectConclusion", [topDefect.DefectName, topDefect.ShiftName, topDefect.Count, topDefect.Share * 100]),
                "topdefect", null));
        }

        if (totalAlarmCount > 0 && result.Count < 5)
        {
            result.Add(new ReviewConclusion(
                L("Rv_AlarmCountConclusion", [totalAlarmCount]),
                "alarmcount", null));
        }

        return result.Take(5).ToList();
    }
}
