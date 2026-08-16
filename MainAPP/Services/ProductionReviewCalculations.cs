using System.Globalization;
using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;

namespace MainAPP.Services;

internal static class ProductionReviewCalculations
{
    /// <summary>正数加 "+" 号，千分位整数（InvariantCulture，与 CSV/PDF 导出口径一致）。ViewModel/PDF 共用。</summary>
    public static string FormatSigned(int value)
        => value > 0 ? $"+{value:N0}" : value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>正数加 "+" 号的百分比增量（InvariantCulture）。</summary>
    public static string FormatSignedPercentage(double value)
        => value > 0 ? $"+{value:P1}" : value.ToString("P1", CultureInfo.InvariantCulture);

    /// <summary>
    /// 计算窗口内产量差分（按班次实例首尾差分）。内部自动排序——高频调用场景（每报警/每状态段）
    /// 请改用 <see cref="CalculateProductionDeltaSorted"/> 并预排序一次，避免重复全量扫描。
    /// </summary>
    public static int CalculateProductionDelta(
        IReadOnlyList<ProductionLog> logs,
        DateTime from,
        DateTime to)
        => CalculateProductionDeltaSorted(logs.OrderBy(log => log.Timestamp).ToList(), from, to);

    /// <summary>
    /// 有序日志上的窗口差分：logs 必须按 Timestamp 升序。二分定位窗口边界，O(log n + m)。
    /// 修复 2026-08-11：复盘页刷新 12.8s（alarmAnalysis 9.2s + timeline.Build 3.4s）根因均为
    /// 对 1.3 万条生产日志反复全量扫描（旧实现每次 Where×2 + OrderBy）。
    /// 语义与旧实现完全一致：窗口内按班次实例切分，每实例取 末值 - 基线（窗口前同班次末条或实例首条）。
    /// </summary>
    public static int CalculateProductionDeltaSorted(
        IReadOnlyList<ProductionLog> logs,
        DateTime from,
        DateTime to)
    {
        if (logs.Count == 0) return 0;

        var lo = LowerBound(logs, from); // 第一个 Timestamp >= from
        var hi = UpperBound(logs, to);   // 第一个 Timestamp > to
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

            // 实例 [instanceStart, i) 收尾：末值 - 基线
            var first = logs[instanceStart];
            var last = logs[i - 1];
            ProductionLog? baseline;
            if (first.Timestamp <= from)
            {
                baseline = first;
            }
            else
            {
                // 窗口前同班次末条：candidates（logs[0..lo)）时间最大者必须匹配班次才有效（旧 FindBaselineBeforeWindow 语义）
                baseline = lo > 0 && logs[lo - 1].ShiftName == first.ShiftName ? logs[lo - 1] : null;
                baseline ??= first;
            }
            total += Math.Max(0, last.OkProduction - baseline.OkProduction);
            total += Math.Max(0, last.NgProduction - baseline.NgProduction);
            instanceStart = i;
        }
        return total;
    }

    /// <summary>第一个 Timestamp &gt;= value 的索引（升序）。</summary>
    private static int LowerBound(IReadOnlyList<ProductionLog> logs, DateTime value)
    {
        int lo = 0, hi = logs.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (logs[mid].Timestamp < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>第一个 Timestamp &gt; value 的索引（升序）。</summary>
    private static int UpperBound(IReadOnlyList<ProductionLog> logs, DateTime value)
    {
        int lo = 0, hi = logs.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (logs[mid].Timestamp <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
