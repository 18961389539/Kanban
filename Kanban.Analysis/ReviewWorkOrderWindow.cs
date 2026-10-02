using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;

namespace Kanban.Analysis;

/// <summary>
/// 复盘窗口内的工单：按实际开停时间与查询区间重叠筛选，不用设备当前工单。
/// </summary>
public static class ReviewWorkOrderWindow
{
    /// <summary>
    /// 选出指定设备在 [from, to] 内实际生产过的工单，按开始时间升序。
    /// 未开始的待产工单不计入。实际开始优先用 StartedAt，旧数据回退计划开始；
    /// 已结束优先用 CompletedAt，旧数据回退计划结束；进行中视为一直持续到窗口之后。
    /// </summary>
    public static IReadOnlyList<WorkOrderDto> Select(
        IEnumerable<WorkOrderDto> orders,
        string deviceId,
        DateTime from,
        DateTime to)
    {
        if (string.IsNullOrEmpty(deviceId) || to < from)
            return [];

        return orders
            .Where(order => string.Equals(order.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(order => (Order: order, Interval: TryInterval(order)))
            .Where(item => item.Interval is { } interval && Overlaps(interval.Start, interval.End, from, to))
            .OrderBy(item => item.Interval!.Value.Start)
            .ThenBy(item => item.Order.OrderNo, StringComparer.Ordinal)
            .Select(item => item.Order)
            .ToList();
    }

    private static bool Overlaps(DateTime start, DateTime end, DateTime from, DateTime to)
        => start < to && end > from;

    private static (DateTime Start, DateTime End)? TryInterval(WorkOrderDto order)
    {
        if (order.Status == WorkOrderStatus.Pending)
            return null;
        // 待产直接中止：没有实际开工，也不要计划区间去凑窗口。
        if (order.Status == WorkOrderStatus.Aborted
            && order.StartedAt is null
            && order.CompletedOkCount is null
            && order.CompletedNgCount is null)
            return null;

        var start = order.StartedAt ?? order.PlannedStart;
        var end = order.Status == WorkOrderStatus.Running
            ? DateTime.MaxValue
            : order.CompletedAt ?? order.PlannedEnd;
        if (end < start)
            end = start;
        return (start, end);
    }
}
