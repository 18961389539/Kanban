using Kanban.Collector.Core.Entities;
using Kanban.Contracts.Dtos;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 工单产量聚合单源：按 WorkOrderId / 设备时间窗口查询生产日志，按班次会话差分累加。
/// OkProduction/NgProduction 为班次会话累计，须差分后才是工单内产量（非 TotalOkProduction 口径）。
/// 同一班次名会跨天重复，计数器复位后新会话从低值重新累计；复位不能把整段首尾相减成负数再截成 0。
/// </summary>
public static class WorkOrderProductionSummaryCalculator
{
    // 日志没有班次实例 ID；采样跨越半天时不能仅凭相同班次名或单调计数认定仍是同一会话。
    private static readonly TimeSpan SessionGap = TimeSpan.FromHours(12);

    public static WorkOrderProductionSummaryDto Empty { get; } = new();

    public static WorkOrderProductionSummaryDto Calculate(WorkOrder workOrder, IProductionHistoryReader history)
    {
        if (HasCompletedSnapshot(workOrder))
        {
            var ok = workOrder.CompletedOkCount!.Value;
            var ng = workOrder.CompletedNgCount!.Value;
            return FromCounts(ok, ng, workOrder.TargetQuantity);
        }

        var logs = history.QueryProductionLogsByWorkOrder(workOrder.Id);
        if (logs.Count == 0)
            logs = history.QueryProductionLogs(FallbackFrom(workOrder), FallbackTo(workOrder), workOrder.DeviceId);

        return logs.Count == 0
            ? Empty
            : CalculateFromLogs(workOrder, logs, history);
    }

    public static WorkOrderProductionSummaryDto CalculateFromLogs(
        WorkOrder workOrder,
        IReadOnlyList<ProductionLog> logs,
        IProductionHistoryReader history)
    {
        var okTotal = 0;
        var ngTotal = 0;
        var runs = SplitSessionRuns(logs);
        ProductionLog? previousRunEnd = null;
        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            var run = runs[runIndex];
            var first = run[0];
            var sameShiftReset = previousRunEnd != null
                && string.Equals(previousRunEnd.ShiftName, first.ShiftName, StringComparison.Ordinal);
            var includeFirst = sameShiftReset;
            if (run.Count == 1 && !sameShiftReset)
            {
                var baseline = history.GetLatestProductionBefore(
                    workOrder.DeviceId, first.Timestamp, first.ShiftName ?? string.Empty);
                var firstBeforeReset = runIndex == 0 && runs.Count > 1
                    && string.Equals(first.ShiftName, runs[1][0].ShiftName, StringComparison.Ordinal);
                if (baseline == null)
                {
                    // 同名会话后续发生复位时，首条无基线快照仍按多条日志的首点处理。
                    includeFirst = !firstBeforeReset;
                }
                else if (IsNewSession(baseline, first))
                {
                    includeFirst = true;
                }
                else
                {
                    okTotal += Math.Max(0, first.OkProduction - baseline.OkProduction);
                    ngTotal += Math.Max(0, first.NgProduction - baseline.NgProduction);
                }
            }

            if (includeFirst)
            {
                okTotal += Math.Max(0, first.OkProduction);
                ngTotal += Math.Max(0, first.NgProduction);
            }

            for (var i = 1; i < run.Count; i++)
            {
                okTotal += Math.Max(0, run[i].OkProduction - run[i - 1].OkProduction);
                ngTotal += Math.Max(0, run[i].NgProduction - run[i - 1].NgProduction);
            }

            previousRunEnd = run[^1];
        }

        return FromCounts(okTotal, ngTotal, workOrder.TargetQuantity);
    }

    /// <summary>班次名变化、任一计数回退或长时间断采都表示新会话；OK/NG 必须共用边界。</summary>
    private static List<List<ProductionLog>> SplitSessionRuns(IReadOnlyList<ProductionLog> logs)
    {
        var runs = new List<List<ProductionLog>>();
        List<ProductionLog>? current = null;
        foreach (var log in logs.OrderBy(p => p.Timestamp).ThenBy(p => p.Id))
        {
            if (current != null
                && (!string.Equals(current[^1].ShiftName ?? string.Empty, log.ShiftName ?? string.Empty, StringComparison.Ordinal)
                    || IsNewSession(current[^1], log)))
            {
                runs.Add(current);
                current = null;
            }

            current ??= new List<ProductionLog>();
            current.Add(log);
        }

        if (current != null)
            runs.Add(current);
        return runs;
    }

    private static bool IsNewSession(ProductionLog previous, ProductionLog current)
        => current.Timestamp - previous.Timestamp >= SessionGap
            || current.OkProduction < previous.OkProduction
            || current.NgProduction < previous.NgProduction;

    private static bool HasCompletedSnapshot(WorkOrder workOrder)
        => workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Aborted
            && workOrder.CompletedOkCount.HasValue
            && workOrder.CompletedNgCount.HasValue;

    private static DateTime FallbackFrom(WorkOrder workOrder) => workOrder.PlannedStart.AddMinutes(-5);

    private static DateTime FallbackTo(WorkOrder workOrder)
        => workOrder.PlannedEnd > workOrder.PlannedStart
            ? workOrder.PlannedEnd.AddMinutes(5)
            : DateTime.Now;

    private static WorkOrderProductionSummaryDto FromCounts(int ok, int ng, int target)
        => new()
        {
            OkCount = ok,
            NgCount = ng,
            AchievementRate = target > 0 ? Math.Min(1.0, (double)ok / target) : 0,
            DefectRate = ok + ng > 0 ? (double)ng / (ok + ng) : 0,
        };
}
