namespace Kanban.Contracts.Metrics;

/// <summary>缺陷历史边界点（窗口差分输入，与存储实体解耦）。</summary>
public readonly record struct DefectHistoryPoint(
    string DefectId,
    string DefectName,
    DateTime Timestamp,
    int Count,
    string ShiftName);

/// <summary>
/// 缺陷计数窗口增量：与复盘页帕累托同口径（末值 − 基线 / 窗口内首末差）。
/// 输入为 <see cref="DefectHistoryPoint"/> 列表（通常来自 QueryWindowBounds）。
/// </summary>
public static class DefectWindowMetrics
{
    /// <summary>
    /// 按（缺陷 + 班次）分组计算窗口内新增件数，再按缺陷 Id 合并（跨班次同缺陷累加）。
    /// </summary>
    public static IReadOnlyDictionary<string, int> ComputeIncrements(
        IReadOnlyList<DefectHistoryPoint> snapshots,
        DateTime from,
        DateTime to)
    {
        if (snapshots.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in snapshots.GroupBy(s => new { s.DefectId, s.ShiftName }))
        {
            var ordered = group.OrderBy(s => s.Timestamp).ToList();
            var window = ordered.Where(s => s.Timestamp >= from && s.Timestamp <= to).ToList();
            if (window.Count == 0) continue;

            var first = window[0];
            var last = window[^1];
            var baselineIndex = ordered.FindLastIndex(s => s.Timestamp < from);
            var count = baselineIndex >= 0
                ? Math.Max(0, last.Count - ordered[baselineIndex].Count)
                : Math.Max(0, last.Count - first.Count);
            if (count <= 0) continue;

            totals.TryGetValue(last.DefectId, out var existing);
            totals[last.DefectId] = existing + count;
        }

        return totals;
    }
}
