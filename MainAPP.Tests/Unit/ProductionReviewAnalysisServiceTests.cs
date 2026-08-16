using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewAnalysisServiceTests
{
    [Fact]
    public void Analyze_BuildsTimelineAndRepeatedAlarmAnalysis()
    {
        var history = new InMemoryHistoryService();
        var service = new ProductionReviewAnalysisService(history);
        var device = new Device
        {
            Id = "device-1",
            Name = "设备1",
            TargetCycle = 100,
        };
        var now = DateTime.Now;
        var from = now.AddHours(-2);
        var to = now;

        history.StatusTransitions.Add(new StatusTransitionRecord
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            PreviousState = (int)DeviceStatus.Unknown,
            CurrentState = (int)DeviceStatus.Running,
            EventTime = from.AddMinutes(5),
        });
        history.StatusTransitions.Add(new StatusTransitionRecord
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            PreviousState = (int)DeviceStatus.Running,
            CurrentState = (int)DeviceStatus.Alarm,
            EventTime = from.AddMinutes(55),
        });
        history.StatusTransitions.Add(new StatusTransitionRecord
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            PreviousState = (int)DeviceStatus.Alarm,
            CurrentState = (int)DeviceStatus.Running,
            EventTime = from.AddMinutes(65),
        });
        history.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            ShiftName = "白班",
            OkProduction = 10,
            Timestamp = from.AddMinutes(10),
        });
        history.ProductionLogs.Add(new ProductionLog
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            ShiftName = "白班",
            OkProduction = 20,
            Timestamp = from.AddMinutes(90),
        });
        for (var index = 0; index < 3; index++)
        {
            var trigger = from.AddMinutes(70 + index * 5);
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                AlarmId = "alarm-1",
                AlarmName = "高温",
                PlcAddress = "M10",
                EventType = AlarmEventType.Triggered,
                EventTime = trigger,
                ShiftName = "白班",
            });
            history.AlarmEvents.Add(new AlarmEventRecord
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                AlarmId = "alarm-1",
                AlarmName = "高温",
                PlcAddress = "M10",
                EventType = AlarmEventType.Recovered,
                EventTime = trigger.AddMinutes(1),
                ShiftName = "白班",
            });
        }

        var result = service.Analyze(
            device,
            history.StatusTransitions,
            history.AlarmEvents,
            history.ProductionLogs,
            from,
            to,
            from.AddHours(-2),
            from);

        Assert.Contains(result.StatusTimeline, segment => segment.StatusText == "报警");
        var alarm = Assert.Single(result.Alarms);
        Assert.Equal(3, alarm.TriggerCount);
        Assert.Equal(2, alarm.TriggerCount - 1);
        Assert.Equal(5, alarm.AverageIntervalMinutes, precision: 1);
        Assert.True(alarm.IsHighFrequency);
        Assert.Contains(result.HealthIssues, issue => issue.StartsWith("节拍异常", StringComparison.Ordinal));
        Assert.True(result.HealthScore < 100);
    }
}
