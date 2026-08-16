using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewDataServiceTests
{
    [Fact]
    public void QueryWindow_ReturnsProductionStatusAndAlarmByDevice()
    {
        var history = new InMemoryHistoryService();
        var now = DateTime.Now;
        history.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = "device-1",
            OkProduction = 10,
            Timestamp = now.AddHours(-2),
        });
        history.StatusTransitions.Add(new StatusTransitionRecord
        {
            DeviceId = "device-1",
            CurrentState = 1,
            EventTime = now.AddMinutes(-30),
        });
        history.AlarmEvents.Add(new AlarmEventRecord
        {
            DeviceId = "device-1",
            AlarmId = "alarm-1",
            EventType = AlarmEventType.Triggered,
            EventTime = now.AddMinutes(-10),
        });

        var service = new ProductionReviewDataService(history);
        var result = service.QueryWindow(now.AddHours(-1), now, ["device-1"]);

        Assert.Single(result.ProductionLogsByDevice["device-1"]);
        Assert.Single(result.StatusTransitionsByDevice["device-1"]);
        Assert.Single(result.AlarmEventsByDevice["device-1"]);
    }

    [Fact]
    public void QueryDeviceRange_ReturnsLatestStatusBeforeWindow()
    {
        var history = new InMemoryHistoryService();
        var from = DateTime.Now.AddHours(-1);
        history.StatusTransitions.Add(new StatusTransitionRecord
        {
            DeviceId = "device-1",
            CurrentState = 1,
            EventTime = from.AddMinutes(-5),
        });

        var service = new ProductionReviewDataService(history);
        var result = service.QueryDeviceRange("device-1", from, DateTime.Now);

        Assert.NotNull(result.LatestStatusBefore);
        Assert.Equal(1, result.LatestStatusBefore!.CurrentState);
    }
}
