using Kanban.Collector.Core.Entities;
using Kanban.Contracts.Dtos;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 工单产量聚合单源：按 WorkOrderId / 设备时间窗口查询生产日志，按班次差分累加。
/// OkProduction/NgProduction 为班次会话累计，须差分后才是工单内产量（非 TotalOkProduction 口径）。
/// </summary>
public static class WorkOrderProductionSummaryCalculator
{
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
        foreach (var group in logs.GroupBy(p => p.ShiftName ?? string.Empty))
        {
            var ordered = group.OrderBy(p => p.Timestamp).ToList();
            if (ordered.Count == 0) continue;
            var first = ordered[0];
            var last = ordered[^1];

            if (ordered.Count == 1)
            {
                var baseline = history.GetLatestProductionBefore(
                    workOrder.DeviceId, first.Timestamp, first.ShiftName ?? string.Empty);
                if (baseline != null)
                {
                    okTotal += Math.Max(0, last.OkProduction - baseline.OkProduction);
                    ngTotal += Math.Max(0, last.NgProduction - baseline.NgProduction);
                }
                else
                {
                    okTotal += Math.Max(0, last.OkProduction);
                    ngTotal += Math.Max(0, last.NgProduction);
                }
            }
            else
            {
                okTotal += Math.Max(0, last.OkProduction - first.OkProduction);
                ngTotal += Math.Max(0, last.NgProduction - first.NgProduction);
            }
        }

        return FromCounts(okTotal, ngTotal, workOrder.TargetQuantity);
    }

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
