using Kanban.Analysis;
using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// OEE 历史分析（Web 端）：从 WPF OeeQueryViewModel + OeeCalculator 移植。
/// 四率公式（合格/性能/可用/OEE）**委托 Kanban.Analysis.OeeCalculator**（ADR-4 单源，
/// 2026-08-13 收敛——此前在本地复制，Core 因 WPF 依赖无法被 WASM 引用，现已去 WPF 化）；
/// 窗口差分产量、状态时长、分班次 OEE（有班次配置时按配置 ResolveRange 切窗，与 WPF OeeQueryViewModel 对齐；
/// 无配置时回退实例首条/末条）、洞察（短板因子 / 班次对比）。
/// </summary>
public static class OeeAnalysis
{

    /// <summary>分班次 OEE 记录。</summary>
    public sealed record ShiftOee(DateTime ShiftTime, string ShiftName, double Quality, double Performance, double Availability, double Oee);

    /// <summary>
    /// 分班次 OEE：按班次实例切分产量快照，每个实例做窗口差分 + 状态时长 → 四率。
    /// 有班次配置时按名称 ResolveRange（与 WPF OeeQueryViewModel 同口径）；否则回退实例首末条。
    /// </summary>
    public static List<ShiftOee> ComputePerShiftOee(
        List<ProductionLogDto> shiftGroupedLogs,
        int targetCycle,
        List<StatusTransitionRecordDto> transitions,
        int initialInitialState,
        DateTime fromDate,
        DateTime toDate,
        DateTime? now = null,
        IReadOnlyList<ShiftConfigDto>? shifts = null)
    {
        List<ShiftOee> result = [];
        // 截断用"当前时刻"：浏览器时区与工厂不同时调用方须传 Dashboard.ServerNow
        var nowValue = now ?? DateTime.Now;

        var sortedTrans = transitions.OrderBy(t => t.EventTime).ToList();

        foreach (var group in ProductionAnalysis.SplitShiftInstances(shiftGroupedLogs))
        {
            var firstLog = group.First();
            var lastLog = group.Last();
            var shiftName = firstLog.ShiftName;

            DateTime shiftFrom;
            DateTime shiftTo;
            var shiftConfig = FindShift(shifts, shiftName);
            if (shiftConfig != null)
            {
                var range = Kanban.Contracts.Metrics.ShiftWindowResolver.ResolveRange(
                    shiftConfig.StartTime, shiftConfig.EndTime, firstLog.Timestamp);
                shiftFrom = range.Start;
                shiftTo = range.End;
                if (shiftFrom < fromDate) shiftFrom = fromDate;
                if (shiftTo > toDate) shiftTo = toDate;
                if (shiftTo > nowValue) shiftTo = nowValue;
            }
            else
            {
                shiftFrom = firstLog.Timestamp;
                shiftTo = lastLog.Timestamp;
                if (shiftTo > nowValue) shiftTo = nowValue;
            }
            if (shiftFrom > toDate) continue;

            // 配置路径下产量仍用实例首末条（抽样点无窗口前基线）；时长窗口按 ResolveRange 与 WPF 对齐
            int okBase = firstLog.OkProduction;
            int ngBase = firstLog.NgProduction;
            int ok = Math.Max(0, lastLog.OkProduction - okBase);
            int ng = Math.Max(0, lastLog.NgProduction - ngBase);

            var subTrans = sortedTrans
                .Where(t => t.EventTime >= shiftFrom && t.EventTime <= shiftTo)
                .ToList();
            int initState = 1;
            var prevBefore = sortedTrans.LastOrDefault(t => t.EventTime < shiftFrom);
            if (prevBefore != null)
                initState = (int)prevBefore.CurrentState;
            else if (shiftFrom <= fromDate) initState = initialInitialState;

            var durations = StatusAnalysis.CalculateStateDurations(subTrans, shiftFrom, shiftTo, initState, now);

            double quality = OeeCalculator.CalculateQualityRate(ok, ng);
            double perf = OeeCalculator.CalculatePerformanceRate(ok, ng, targetCycle, durations.RunTime);
            double avail = OeeCalculator.CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
            double oee = OeeCalculator.CalculateOee(quality, perf, avail);

            result.Add(new ShiftOee(shiftFrom, shiftName, quality, perf, avail, oee));
        }

        return result;
    }

    private static ShiftConfigDto? FindShift(IReadOnlyList<ShiftConfigDto>? shifts, string? name)
    {
        if (shifts == null || string.IsNullOrEmpty(name))
            return null;
        foreach (var s in shifts)
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    /// <summary>OEE 洞察：短板因子检测 + 平衡口径 + 班次对比（差距 ≥10pp 时定位拖累项）。</summary>
    public static string? BuildInsight(
        double q, double p, double a, List<ShiftOee> perShiftOee,
        Func<string, object[], string> localize)
    {
        if (q == 0 && p == 0 && a == 0) return null;

        var items = new[]
        {
            (Name: localize("Lbl_Quality", []), Value: q),
            (Name: localize("Lbl_Performance", []), Value: p),
            (Name: localize("Lbl_Availability", []), Value: a),
        };
        var min = items.MinBy(x => x.Value);
        var max = items.MaxBy(x => x.Value);

        string main;
        if (min.Value < 0.6)
            main = localize("Hq_InsOeeWeak", [min.Name, min.Value]);
        else if (min.Value < max.Value - 0.1)
            main = localize("Hq_InsOeeGap", [max.Name, max.Value, min.Name, min.Value]);
        else
            main = localize("Hq_InsOeeBalance", [q, p, a, q * p * a]);

        var shiftInsight = BuildShiftComparisonInsight(perShiftOee, localize);
        return shiftInsight == null ? main : $"{main}\n{shiftInsight}";
    }

    private static string? BuildShiftComparisonInsight(List<ShiftOee> perShiftOee,
        Func<string, object[], string> localize)
    {
        if (perShiftOee.Count < 2) return null;

        var worst = perShiftOee.MinBy(s => s.Oee);
        var best = perShiftOee.MaxBy(s => s.Oee);
        if (worst == null || best == null || worst.Oee >= best.Oee) return null;

        var gap = best.Oee - worst.Oee;
        if (gap < 0.10) return null; // 差距 < 10pp 视为正常波动

        var factors = new[]
        {
            (Name: localize("Lbl_Quality", []), Diff: worst.Quality - best.Quality, Worst: worst.Quality, Best: best.Quality),
            (Name: localize("Lbl_Performance", []), Diff: worst.Performance - best.Performance, Worst: worst.Performance, Best: best.Performance),
            (Name: localize("Lbl_Availability", []), Diff: worst.Availability - best.Availability, Worst: worst.Availability, Best: best.Availability),
        };
        var drag = factors.MinBy(f => f.Diff);

        var timeLabel = worst.ShiftTime.ToString("MM-dd HH:mm");
        var dragHint = drag!.Diff < -0.05
            ? localize("Hq_InsShiftDrag", [drag.Name, drag.Worst, drag.Best])
            : "";
        return localize("Hq_InsShiftWorst", [worst.ShiftName, timeLabel, worst.Oee, gap]) +
               (string.IsNullOrEmpty(dragHint) ? "" : "\n" + dragHint);
    }
}
