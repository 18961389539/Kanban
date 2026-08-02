using MainAPP.Entities;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ProductionReviewComponentServiceTests
{
    [Fact]
    public void AlarmAnalysis_IdentifiesRepeatedHighFrequencyAlarm()
    {
        var service = new ProductionReviewAlarmAnalysisService();
        var now = DateTime.Now;
        var events = Enumerable.Range(0, 3)
            .SelectMany(index => new[]
            {
                new AlarmEventRecord
                {
                    AlarmName = "高温",
                    DeviceName = "设备1",
                    PlcAddress = "M10",
                    EventType = AlarmEventType.Triggered,
                    EventTime = now.AddMinutes(index * 5),
                },
                new AlarmEventRecord
                {
                    AlarmName = "高温",
                    DeviceName = "设备1",
                    PlcAddress = "M10",
                    EventType = AlarmEventType.Recovered,
                    EventTime = now.AddMinutes(index * 5 + 1),
                },
            })
            .ToList();

        var result = service.Analyze(events, []);

        var alarm = Assert.Single(result);
        Assert.Equal(3, alarm.TriggerCount);
        Assert.Equal(5, alarm.AverageIntervalMinutes, precision: 1);
        Assert.True(alarm.IsHighFrequency);
    }

    [Fact]
    public void StatusTimeline_CreatesStateIntervalsWithOutputAndAlarmCount()
    {
        var service = new ProductionReviewStatusTimelineService();
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Now;
        var transitions = new List<StatusTransitionRecord>
        {
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Running, EventTime = from.AddMinutes(5) },
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Alarm, EventTime = from.AddMinutes(30) },
        };
        var alarms = new List<AlarmEventRecord>
        {
            new() { EventType = AlarmEventType.Triggered, EventTime = from.AddMinutes(35) },
        };
        var production = new List<ProductionLog>
        {
            new() { DeviceId = "d1", ShiftName = "白班", OkProduction = 10, Timestamp = from.AddMinutes(10) },
            new() { DeviceId = "d1", ShiftName = "白班", OkProduction = 20, Timestamp = from.AddMinutes(25) },
        };

        var result = service.Build("d1", transitions, alarms, production, from, (int)DeviceStatus.Unknown, from, to);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, segment => segment.StatusText == "报警" && segment.AlarmCount == 1);
        Assert.Contains(result, segment => segment.OutputDelta > 0);
    }

    [Fact]
    public void HealthScore_DeductsForAllRequestedAnomalies()
    {
        var service = new ProductionReviewHealthScoreService();
        var device = new Device { TargetCycle = 100 };
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Now;
        var timeline = new List<ReviewStatusSegmentData>
        {
            new(from, from.AddMinutes(40), "运行", (int)DeviceStatus.Running, 0, 0, true),
        };
        var alarms = Enumerable.Range(0, 3).Select(_ => new AlarmEventRecord
        {
            EventType = AlarmEventType.Triggered,
        }).ToList();
        var defects = new List<ReviewDefectConcentrationData>
        {
            new("划痕", "白班", "08-01 10:00", 3, 1),
        };

        var (issues, score) = service.Calculate(device, [], alarms, timeline, defects, 0, 0, from, to);

        Assert.Equal(4, issues.Count);
        Assert.Equal(0, score);
    }
}
