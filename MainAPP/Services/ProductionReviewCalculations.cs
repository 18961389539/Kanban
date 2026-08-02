using MainAPP.Entities;

namespace MainAPP.Services;

internal static class ProductionReviewCalculations
{
    public static int CalculateProductionDelta(
        IReadOnlyList<ProductionLog> logs,
        DateTime from,
        DateTime to)
    {
        var inWindow = logs.Where(log => log.Timestamp >= from && log.Timestamp <= to).ToList();
        var baselineCandidates = logs.Where(log => log.Timestamp < from).ToList();
        if (inWindow.Count == 0) return 0;

        var groups = SplitShiftInstances(inWindow);
        var total = 0;
        foreach (var group in groups)
        {
            var first = group[0];
            var last = group[^1];
            var baseline = first.Timestamp <= from
                ? first
                : FindBaselineBeforeWindow(baselineCandidates, first.ShiftName) ?? first;
            total += Math.Max(0, last.OkProduction - baseline.OkProduction);
            total += Math.Max(0, last.NgProduction - baseline.NgProduction);
        }
        return total;
    }

    private static List<List<ProductionLog>> SplitShiftInstances(List<ProductionLog> sortedLogs)
    {
        var groups = new List<List<ProductionLog>>();
        var current = new List<ProductionLog>();
        foreach (var log in sortedLogs.OrderBy(log => log.Timestamp))
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                if (log.OkProduction < previous.OkProduction
                    || log.NgProduction < previous.NgProduction
                    || log.ShiftName != previous.ShiftName)
                {
                    groups.Add(current);
                    current = [];
                }
            }
            current.Add(log);
        }
        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    private static ProductionLog? FindBaselineBeforeWindow(
        IReadOnlyList<ProductionLog> candidates,
        string shiftName)
    {
        var seenDifferentShift = false;
        foreach (var log in candidates.OrderByDescending(log => log.Timestamp))
        {
            if (log.ShiftName == shiftName)
                return seenDifferentShift ? null : log;
            seenDifferentShift = true;
        }
        return null;
    }
}
