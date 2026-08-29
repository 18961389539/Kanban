using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
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
                    DeviceId = "d1",
                    AlarmName = "高温",
                    DeviceName = "设备1",
                    PlcAddress = "M10",
                    EventType = AlarmEventType.Triggered,
                    EventTime = now.AddMinutes(index * 5),
                },
                new AlarmEventRecord
                {
                    DeviceId = "d1",
                    AlarmName = "高温",
                    DeviceName = "设备1",
                    PlcAddress = "M10",
                    EventType = AlarmEventType.Recovered,
                    EventTime = now.AddMinutes(index * 5 + 1),
                },
            })
            .ToList();

        var result = service.Analyze(events, [], now.AddHours(2));

        var alarm = Assert.Single(result);
        Assert.Equal(3, alarm.TriggerCount);
        Assert.Equal(5, alarm.AverageIntervalMinutes, precision: 1);
        Assert.True(alarm.IsHighFrequency);
    }

    [Fact]
    public void AlarmAnalysis_FiltersByDeviceWhenDeviceIdProvided()
    {
        var service = new ProductionReviewAlarmAnalysisService();
        var now = DateTime.Now;
        var events = new List<AlarmEventRecord>
        {
            new()
            {
                DeviceId = "d1", AlarmName = "高温", DeviceName = "设备1", PlcAddress = "M10",
                EventType = AlarmEventType.Triggered, EventTime = now,
            },
            new()
            {
                DeviceId = "d2", AlarmName = "高温", DeviceName = "设备2", PlcAddress = "M10",
                EventType = AlarmEventType.Triggered, EventTime = now,
            },
        };

        var result = service.Analyze(events, [], now.AddHours(1), "d1");

        var alarm = Assert.Single(result);
        Assert.Equal("设备1", alarm.DeviceName);
        Assert.Equal(1, alarm.TriggerCount);
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
            new() { DeviceId = "d1", EventType = AlarmEventType.Triggered, EventTime = from.AddMinutes(35) },
        };
        var production = new List<ProductionLog>
        {
            new() { DeviceId = "d1", ShiftName = "白班", OkProduction = 10, Timestamp = from.AddMinutes(10) },
            new() { DeviceId = "d1", ShiftName = "白班", OkProduction = 20, Timestamp = from.AddMinutes(25) },
        };

        var result = service.Build("d1", transitions, alarms, production, (int)DeviceStatus.Offline, from, to);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, segment => segment.StatusText == "报警" && segment.AlarmCount == 1);
        Assert.Contains(result, segment => segment.OutputDelta > 0);
    }

    [Fact]
    public void StatusTimeline_AlarmCountFiltersByDevice()
    {
        var service = new ProductionReviewStatusTimelineService();
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Now;
        var transitions = new List<StatusTransitionRecord>
        {
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Running, EventTime = from.AddMinutes(1) },
        };
        // d2 的报警不得计入 d1 的状态段
        var alarms = new List<AlarmEventRecord>
        {
            new() { DeviceId = "d1", EventType = AlarmEventType.Triggered, EventTime = from.AddMinutes(10) },
            new() { DeviceId = "d2", EventType = AlarmEventType.Triggered, EventTime = from.AddMinutes(20) },
        };

        var result = service.Build("d1", transitions, alarms, [], (int)DeviceStatus.Running, from, to);

        Assert.Equal(1, result.Sum(segment => segment.AlarmCount));
    }

    [Fact]
    public void StatusTimeline_MergesSubMinuteJitterIntoPreviousSegment_NoGaps()
    {
        var service = new ProductionReviewStatusTimelineService();
        var from = DateTime.Now.AddHours(-1);
        var to = DateTime.Now;
        var jitter = from.AddMinutes(10).AddSeconds(5);
        var transitions = new List<StatusTransitionRecord>
        {
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Running, EventTime = from.AddMinutes(10) },
            // 5 秒抖动段（<0.1 分钟）：应并入前一段，而不是被丢弃留下时间线空洞
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Alarm, EventTime = jitter },
            new() { DeviceId = "d1", CurrentState = (int)DeviceStatus.Running, EventTime = from.AddMinutes(20) },
        };

        var result = service.Build("d1", transitions, [], [], (int)DeviceStatus.Running, from, to);

        // 3 段：[from,jitter) Running（含抖动）、[jitter,20min) Alarm、[20min,to) Running；段间连续无空洞
        Assert.Equal(3, result.Count);
        Assert.Equal((int)DeviceStatus.Running, result[0].StatusWord);
        Assert.Equal((int)DeviceStatus.Alarm, result[1].StatusWord);
        for (var i = 1; i < result.Count; i++)
            Assert.Equal(result[i - 1].End, result[i].Start);
        Assert.True((result[0].End - result[0].Start).TotalMinutes >= 0.1);
    }

    [Fact]
    public void HeatmapBuckets_ClipLastBucketToWindowEnd()
    {
        var start = new DateTime(2026, 8, 10, 8, 0, 0);
        // 500 分钟窗口：桶宽 30 分钟 → 17 桶，最后一个桶 [495,510) 越界需裁剪到 [495,500)
        var segments = new List<ReviewStatusSegment>
        {
            new() { Start = start, End = start.AddMinutes(500), StatusWord = (int)DeviceStatus.Running },
        };

        var buckets = HeatmapBucketBuilder.Build(segments);

        Assert.Equal(17, buckets.Count);
        Assert.Equal(start.AddMinutes(500), buckets[^1].End);
        Assert.Equal((int)DeviceStatus.Running, buckets[^1].StatusWord);
        Assert.NotNull(buckets[^1].SourceSegment);
    }

    [Fact]
    public void HeatmapBuckets_CellColorAndSourceSegmentShareLongestOverlap()
    {
        var start = new DateTime(2026, 8, 10, 8, 0, 0);
        // 桶 [8:00,8:15)：10 分钟 Running + 5 分钟 Alarm → 着色为 Running，SourceSegment 也必须是 Running 段
        var running = new ReviewStatusSegment
        {
            Start = start, End = start.AddMinutes(10), StatusWord = (int)DeviceStatus.Running,
        };
        var alarm = new ReviewStatusSegment
        {
            Start = start.AddMinutes(10), End = start.AddMinutes(30), StatusWord = (int)DeviceStatus.Alarm,
        };

        var buckets = HeatmapBucketBuilder.Build([running, alarm]);

        var first = buckets[0];
        Assert.Equal((int)DeviceStatus.Running, first.StatusWord);
        Assert.Same(running, first.SourceSegment);
    }

    [Fact]
    public void HeatmapBuckets_TickLabelsShowTimeOnSingleDayAndDateAcrossDays()
    {
        var start = new DateTime(2026, 8, 10, 8, 0, 0);
        var singleDay = new List<ReviewStatusSegment>
        {
            new() { Start = start, End = start.AddHours(8), StatusWord = (int)DeviceStatus.Running },
        };
        var crossDay = new List<ReviewStatusSegment>
        {
            new() { Start = start, End = start.AddDays(2), StatusWord = (int)DeviceStatus.Running },
        };

        var singleBuckets = HeatmapBucketBuilder.Build(singleDay);
        var crossBuckets = HeatmapBucketBuilder.Build(crossDay);

        Assert.Equal("08:00", singleBuckets[0].TickLabel);
        Assert.Equal("08-10", crossBuckets[0].TickLabel);
        Assert.Null(singleBuckets[1].TickLabel);
        Assert.All(crossBuckets.Where(bucket => bucket.TickLabel != null),
            bucket => Assert.Matches(@"^\d{2}-\d{2}$", bucket.TickLabel));
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
