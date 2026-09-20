using Kanban.Contracts.Dtos;

namespace Kanban.Analysis;

/// <summary>
/// 产量窗口差分 / 15 分钟抽样（Web 历史查询与 Collector 窗口分析单源）。
/// 口径对齐 WPF HistoryQueryHelper：班次实例切分、窗口末累计 − 同实例基线。
/// </summary>
public static class ProductionWindowMetrics
{
    public static List<List<ProductionLogDto>> SplitShiftInstances(List<ProductionLogDto> sortedLogs)
    {
        List<List<ProductionLogDto>> groups = [];
        List<ProductionLogDto> cur = [];
        foreach (var p in sortedLogs)
        {
            if (cur.Count > 0)
            {
                var prev = cur[^1];
                if (p.OkProduction < prev.OkProduction
                    || p.NgProduction < prev.NgProduction
                    || p.ShiftName != prev.ShiftName)
                {
                    groups.Add(cur);
                    cur = [];
                }
            }
            cur.Add(p);
        }
        if (cur.Count > 0)
            groups.Add(cur);
        return groups;
    }

    public static (int Ok, int Ng) SumWindowProduction(
        List<ProductionLogDto> logsInWindow,
        List<ProductionLogDto> baselineCandidates,
        DateTime windowFrom)
    {
        if (logsInWindow.Count == 0)
            return (0, 0);
        var all = new List<ProductionLogDto>(logsInWindow.Count + baselineCandidates.Count);
        all.AddRange(logsInWindow);
        all.AddRange(baselineCandidates);
        int ok = 0, ng = 0;
        foreach (var group in SplitShiftInstances(all.OrderBy(p => p.Timestamp).ToList()))
        {
            var winPart = group.Where(p => p.Timestamp >= windowFrom).ToList();
            if (winPart.Count == 0)
                continue;
            var last = winPart[^1];
            ProductionLogDto baseRec;
            if (winPart[0].Timestamp <= windowFrom)
            {
                baseRec = winPart[0];
            }
            else
            {
                var before = group.LastOrDefault(p => p.Timestamp < windowFrom);
                baseRec = before ?? winPart[0];
            }
            ok += Math.Max(0, last.OkProduction - baseRec.OkProduction);
            ng += Math.Max(0, last.NgProduction - baseRec.NgProduction);
        }
        return (ok, ng);
    }

    /// <summary>每班次实例、每 15 分钟桶保留桶末一条（图表与 WASM 传输压缩）。</summary>
    public static List<ProductionLogDto> Sample15Min(List<ProductionLogDto> logs)
    {
        if (logs.Count == 0)
            return [];
        return SplitShiftInstances(logs.OrderBy(p => p.Timestamp).ToList())
            .SelectMany(g => g
                .GroupBy(p => new DateTime(
                    p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day,
                    p.Timestamp.Hour, p.Timestamp.Minute / 15 * 15, 0))
                .OrderBy(b => b.Key)
                .Select(b => b.MaxBy(p => p.Timestamp)!))
            .OrderBy(p => p.Timestamp)
            .ToList();
    }

    public static List<(DateTime Time, int Ok, int Ng)> BuildChartData(List<ProductionLogDto> logs)
        => Sample15Min(logs).Select(p => (p.Timestamp, p.OkProduction, p.NgProduction)).ToList();
}
