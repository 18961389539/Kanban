using Kanban.Contracts.Dtos;

namespace Kanban.Web.Services;

/// <summary>
/// OEE 历史分析（Web 端）：从 WPF OeeQueryViewModel + OeeCalculator 移植。
/// 四率公式（合格/性能/可用/OEE）、窗口差分产量、状态时长、分班次 OEE（Web 无班次配置 API，走
/// WPF 的"无配置回退路径"：班次范围 = 实例首条/末条时间）、洞察（短板因子 / 班次对比）。
/// </summary>
public static class OeeAnalysis
{
    public static double CalculateQualityRate(int ok, int ng)
    {
        var total = ok + ng;
        return total > 0 ? Clamp((double)ok / total) : 0;
    }

    /// <summary>性能率 = 累计实际产量 /（目标节拍 × 运行小时）。</summary>
    public static double CalculatePerformanceRate(int totalOk, int totalNg, int targetCycle, double runTimeSeconds)
    {
        if (targetCycle <= 0 || runTimeSeconds <= 0) return 0;
        var idealOutput = targetCycle * (runTimeSeconds / 3600.0);
        return idealOutput > 0 ? Clamp((totalOk + totalNg) / idealOutput) : 0;
    }

    /// <summary>可用率 = 运行时间 /（运行时间 + 报警时间）；业务口径不含待机。</summary>
    public static double CalculateAvailabilityRate(double runTime, double alarmTime)
    {
        var denom = runTime + alarmTime;
        return denom > 0 ? Clamp(runTime / denom) : 0;
    }

    public static double CalculateOee(double q, double p, double a) => Clamp(q * p * a);

    private static double Clamp(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    /// <summary>分班次 OEE 记录。</summary>
    public sealed record ShiftOee(DateTime ShiftTime, string ShiftName, double Quality, double Performance, double Availability, double Oee);

    /// <summary>
    /// 分班次 OEE：按班次实例切分产量快照，每个实例做窗口差分 + 状态时长 → 四率。
    /// Web 端无班次配置（AppSettings.Shifts 在 Collector 侧、无 Hub API），走 WPF 的无配置回退：
    /// shiftFrom = 实例首条时间（窗口差分基准即实例首条累计值，无需外部基准取数），
    /// shiftTo = min(实例末条时间, now)。
    /// </summary>
    public static List<ShiftOee> ComputePerShiftOee(
        List<ProductionLogDto> shiftGroupedLogs,
        int targetCycle,
        List<StatusTransitionRecordDto> transitions,
        int initialInitialState,
        DateTime fromDate,
        DateTime toDate)
    {
        List<ShiftOee> result = [];
        var now = DateTime.Now;

        var sortedTrans = transitions.OrderBy(t => t.EventTime).ToList();

        foreach (var group in ProductionAnalysis.SplitShiftInstances(shiftGroupedLogs))
        {
            var firstLog = group.First();
            var lastLog = group.Last();
            var shiftName = firstLog.ShiftName;

            // 无班次配置回退：班次范围 = 实例首条 ~ 末条（截到当前时刻）
            var shiftFrom = firstLog.Timestamp;
            var shiftTo = lastLog.Timestamp;
            if (shiftTo > now) shiftTo = now;
            if (shiftFrom > toDate) continue;

            // 回退路径下 shiftFrom == 实例首条时间 → 差分基准即首条累计值（班次内累计自班次起始重置）
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

            var durations = StatusAnalysis.CalculateStateDurations(subTrans, shiftFrom, shiftTo, initState);

            double quality = CalculateQualityRate(ok, ng);
            double perf = CalculatePerformanceRate(ok, ng, targetCycle, durations.RunTime);
            double avail = CalculateAvailabilityRate(durations.RunTime, durations.AlarmTime);
            double oee = CalculateOee(quality, perf, avail);

            result.Add(new ShiftOee(shiftFrom, shiftName, quality, perf, avail, oee));
        }

        return result;
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
