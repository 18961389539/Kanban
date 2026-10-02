using Kanban.Analysis;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Xunit;

namespace MainAPP.Tests.Unit;

public class ReviewWorkOrderWindowTests
{
    private static readonly DateTime From = new(2026, 8, 8, 8, 0, 0);
    private static readonly DateTime To = new(2026, 8, 8, 20, 0, 0);

    [Fact]
    public void Select_IncludesOrdersWhoseActualRunOverlapsWindow()
    {
        var orders = new[]
        {
            Order("early", WorkOrderStatus.Completed, From.AddHours(-10), From.AddHours(-1)),
            Order("overlap", WorkOrderStatus.Completed, From.AddHours(-1), From.AddHours(2)),
            Order("inside", WorkOrderStatus.Completed, From.AddHours(1), From.AddHours(3)),
            Order("after", WorkOrderStatus.Completed, To.AddHours(1), To.AddHours(4)),
            Order("other-device", WorkOrderStatus.Completed, From, To, deviceId: "dev2"),
        };

        var selected = ReviewWorkOrderWindow.Select(orders, "dev1", From, To);

        Assert.Equal(["overlap", "inside"], selected.Select(order => order.OrderNo));
    }

    [Fact]
    public void Select_SkipsPendingAndAbortedBeforeStart()
    {
        var orders = new[]
        {
            Order("pending", WorkOrderStatus.Pending, null, null),
            Order("aborted-pending", WorkOrderStatus.Aborted, null, From.AddHours(1)),
            Order("running", WorkOrderStatus.Running, From.AddHours(1), null),
        };

        var selected = ReviewWorkOrderWindow.Select(orders, "dev1", From, To);

        Assert.Equal(["running"], selected.Select(order => order.OrderNo));
    }

    [Fact]
    public void Select_LegacyCompletedOrder_UsesPlannedInterval()
    {
        var legacy = Order("legacy", WorkOrderStatus.Completed, null, null);
        legacy = legacy with
        {
            PlannedStart = From.AddHours(2),
            PlannedEnd = From.AddHours(6),
        };

        var selected = ReviewWorkOrderWindow.Select([legacy], "dev1", From, To);

        Assert.Equal("legacy", Assert.Single(selected).OrderNo);
    }

    private static WorkOrderDto Order(
        string orderNo,
        WorkOrderStatus status,
        DateTime? startedAt,
        DateTime? completedAt,
        string deviceId = "dev1") => new()
    {
        OrderNo = orderNo,
        ProductCode = "P",
        ProductName = "产品",
        DeviceId = deviceId,
        DeviceName = "设备",
        Status = status,
        PlannedStart = startedAt ?? From,
        PlannedEnd = completedAt ?? To,
        StartedAt = startedAt,
        CompletedAt = status == WorkOrderStatus.Running ? null : completedAt,
    };
}
