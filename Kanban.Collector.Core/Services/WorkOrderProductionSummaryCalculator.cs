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
        foreach (var run in SplitConsecutiveShiftRuns(logs))
        {
            if (run.Count == 1)
            {
                var sample = run[0];
                var baseline = history.GetLatestProductionBefore(
                    workOrder.DeviceId, sample.Timestamp, sample.ShiftName ?? string.Empty);
                okTotal += CountFromBaseline(baseline?.OkProduction, sample.OkProduction);
                ngTotal += CountFromBaseline(baseline?.NgProduction, sample.NgProduction);
                continue;
            }

            okTotal += AccumulateSession(run, static log => log.OkProduction);
            ngTotal += AccumulateSession(run, static log => log.NgProduction);
        }

        return FromCounts(okTotal, ngTotal, workOrder.TargetQuantity);
    }

    /// <summary>
    /// 按时间切成连续的同名班次段。班次名变化就切开，避免把隔天的「白班」并成一条首尾相减。
    /// </summary>
    private static List<List<ProductionLog>> SplitConsecutiveShiftRuns(IReadOnlyList<ProductionLog> logs)
    {
        var runs = new List<List<ProductionLog>>();
        List<ProductionLog>? current = null;
        foreach (var log in logs.OrderBy(p => p.Timestamp).ThenBy(p => p.Id))
        {
            if (current != null
                && !string.Equals(current[^1].ShiftName ?? string.Empty, log.ShiftName ?? string.Empty, StringComparison.Ordinal))
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

    /// <summary>
    /// 同一班次段内累加会话增量。计数器回退表示新会话开始，已累计的产量保留，并计入新会话当前值。
    /// </summary>
    private static int AccumulateSession(List<ProductionLog> run, Func<ProductionLog, int> value)
    {
        var total = 0;
        var previous = value(run[0]);
        for (var i = 1; i < run.Count; i++)
        {
            var current = value(run[i]);
            if (current >= previous)
                total += current - previous;
            else if (current > 0)
                total += current;
            previous = current;
        }

        return total;
    }

    /// <summary>
    /// 单条样本：基线不高于当前值时做差分；基线更高说明会话已复位，改计当前会话累计，避免出现负数。
    /// </summary>
    private static int CountFromBaseline(int? baseline, int current)
    {
        if (current <= 0) return 0;
        if (baseline is int start && start >= 0 && start <= current)
            return current - start;
        return current;
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
