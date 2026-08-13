using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Web.Services;

/// <summary>
/// 报警历史分析（Web 端）：从 WPF AlarmQueryViewModel 移植。
/// 计数（触发/恢复/待恢复）、按报警名统计（频次/平均时长）、三类洞察（TOP3 / 连锁触发 / 待恢复时长排行）。
/// </summary>
public static class AlarmAnalysis
{
    /// <summary>报警统计：按报警名分组的频次与平均持续时长（仅 Triggered→Recovered 配对计入时长）。</summary>
    public static List<(string AlarmName, int TriggerCount, double AvgDurationMin)> BuildStats(
        List<AlarmEventRecordDto> events)
    {
        // 班次切换事件（ShiftChange）表示"报警在新班次重新开始计时"，不是物理恢复。
        // 仅 Recovered 事件计入时长差分，避免跨班次报警被错误压缩为"在班次切换点恢复"。
        var allRecovers = events
            .Where(e => e.EventType == AlarmEventType.Recovered)
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .ToDictionary(k => k.Key, v => v.OrderBy(e => e.EventTime).ToList());

        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => e.AlarmName)
            .Select(g =>
            {
                var triggers = g.OrderBy(e => e.EventTime).ToList();
                List<double> durations = [];
                foreach (var t in triggers)
                {
                    var key = new { t.DeviceId, t.AlarmId };
                    if (!allRecovers.TryGetValue(key, out var recList)) continue;
                    var recovery = recList.FirstOrDefault(r => r.EventTime > t.EventTime);
                    if (recovery == null) continue;
                    durations.Add((recovery.EventTime - t.EventTime).TotalMinutes);
                }
                return (
                    AlarmName: g.Key,
                    TriggerCount: triggers.Count,
                    AvgDurationMin: durations.Count > 0 ? durations.Average() : 0);
            })
            .OrderByDescending(x => x.TriggerCount)
            .ToList();
    }

    /// <summary>待恢复计数：最近事件为 Triggered 的 (DeviceId, AlarmId) 组数（ShiftChange 末事件不算待恢复）。</summary>
    public static int CountPending(List<AlarmEventRecordDto> events)
        => events
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .Count(g => g.OrderByDescending(e => e.EventTime).First().EventType == AlarmEventType.Triggered);

    /// <summary>
    /// 报警洞察（按需本地化）：TOP3 频次 + 连锁触发模式（5 分钟窗口，≥2 次才报告）+ 待恢复时长排行 Top3。
    /// </summary>
    public static string? BuildInsight(
        List<(string AlarmName, int TriggerCount, double AvgDurationMin)> stats,
        int totalTriggers,
        List<AlarmEventRecordDto> allEvents,
        DateTime effectiveTo,
        Func<string, object[], string> localize)
    {
        if (stats.Count == 0 || totalTriggers <= 0) return null;

        var top3 = stats.Take(3).Select(t =>
        {
            var ratio = (double)t.TriggerCount / totalTriggers;
            return localize("Hq_InsAlarmTop", [t.AlarmName, t.TriggerCount, ratio]);
        });
        var sb = new System.Text.StringBuilder(localize("Hq_InsAlarmTop3", [string.Join(" / ", top3)]));

        var correlation = BuildCorrelationInsight(allEvents, localize);
        if (correlation != null) sb.Append('\n').Append(correlation);

        var pending = BuildPendingInsight(allEvents, effectiveTo, localize);
        if (pending != null) sb.Append('\n').Append(pending);

        return sb.ToString();
    }

    /// <summary>连锁触发检测：同一 5 分钟窗口内多个不同报警先后触发，组合出现 ≥2 次才报告。</summary>
    private static string? BuildCorrelationInsight(List<AlarmEventRecordDto> allEvents,
        Func<string, object[], string> localize)
    {
        var window = TimeSpan.FromMinutes(5);
        var triggers = allEvents
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .OrderBy(e => e.EventTime)
            .ToList();
        if (triggers.Count < 2) return null;

        List<List<string>> chains = [];
        for (int i = 0; i < triggers.Count; i++)
        {
            var seed = triggers[i];
            List<string> chain = [seed.AlarmName ?? "?"];
            for (int j = i + 1; j < triggers.Count; j++)
            {
                if (triggers[j].EventTime - seed.EventTime > window) break;
                var name = triggers[j].AlarmName ?? "?";
                if (!chain.Contains(name)) chain.Add(name);
            }
            if (chain.Count >= 2) chains.Add(chain);
        }
        if (chains.Count == 0) return null;

        var groups = chains
            .GroupBy(c => string.Join("→", c))
            .Select(g => (Pattern: g.Key, Count: g.Count()))
            .Where(x => x.Count >= 2)
            .OrderByDescending(x => x.Count)
            .ToList();
        if (groups.Count == 0) return null;

        var top = groups.First();
        return localize("Hq_InsAlarmChain", [top.Pattern, top.Count]);
    }

    /// <summary>待恢复持续时长排行：末事件为 Triggered 且其后无 Recovered 的报警，按触发至 effectiveTo 时长降序 Top3。</summary>
    private static string? BuildPendingInsight(List<AlarmEventRecordDto> allEvents, DateTime effectiveTo,
        Func<string, object[], string> localize)
    {
        var triggers = allEvents.Where(e => e.EventType == AlarmEventType.Triggered).ToList();
        var recovers = allEvents.Where(e => e.EventType == AlarmEventType.Recovered).ToList();

        List<(string AlarmName, DateTime TriggerTime, double Minutes)> pending = [];
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
                pending.Add((g.Key.AlarmName ?? "?", lastTrigger.EventTime, minutes));
        }
        if (pending.Count == 0) return null;

        var top = pending.OrderByDescending(x => x.Minutes).Take(3).Select(p =>
        {
            var dur = p.Minutes >= 60 ? $"{p.Minutes / 60.0:F1}h" : $"{p.Minutes:F0}min";
            return localize("Hq_InsAlarmPendingItem", [p.AlarmName, dur, p.TriggerTime.ToString("MM-dd HH:mm")]);
        });
        return localize("Hq_InsAlarmPending", [string.Join(" / ", top)]);
    }

    /// <summary>报警排行项（按 (报警名, 设备名) 分组）。</summary>
    public sealed record AlarmTop(string AlarmName, string DeviceName, int TriggerCount, double TotalDurationMinutes);

    /// <summary>
    /// Top 排行：按 (报警名, 设备名) 分组统计触发次数与累计持续时长（Triggered→首个后续 Recovered 配对）。
    /// 与 WPF AlarmCenterViewModel.RefreshStats 的 Top N 逻辑一致（时长口径：同组配对求和）。
    /// </summary>
    public static List<AlarmTop> BuildTopStats(List<AlarmEventRecordDto> events)
    {
        var recovers = events
            .Where(e => e.EventType == AlarmEventType.Recovered)
            .GroupBy(e => new { e.DeviceId, e.AlarmId })
            .ToDictionary(k => k.Key, v => v.OrderBy(e => e.EventTime).ToList());

        return events
            .Where(e => e.EventType == AlarmEventType.Triggered)
            .GroupBy(e => new { e.AlarmName, e.DeviceName })
            .Select(g =>
            {
                double totalMinutes = 0;
                foreach (var t in g.OrderBy(e => e.EventTime))
                {
                    var key = new { t.DeviceId, t.AlarmId };
                    if (!recovers.TryGetValue(key, out var recList)) continue;
                    var recovery = recList.FirstOrDefault(r => r.EventTime > t.EventTime);
                    if (recovery == null) continue;
                    totalMinutes += (recovery.EventTime - t.EventTime).TotalMinutes;
                }
                return new AlarmTop(g.Key.AlarmName, g.Key.DeviceName, g.Count(), totalMinutes);
            })
            .OrderByDescending(x => x.TotalDurationMinutes)
            .ToList();
    }

    /// <summary>报警事件类型 → 三语文本（对齐 WPF HistoryQueryHelper.GetEventTypeText）。</summary>
    public static string GetEventTypeText(AlarmEventType type, Func<string, object[], string> localize) => type switch
    {
        AlarmEventType.Triggered => localize("Hq_AlarmTriggered", []),
        AlarmEventType.Recovered => localize("Hq_AlarmRecovered", []),
        _ => localize("Hq_ShiftChange", []),
    };
}
