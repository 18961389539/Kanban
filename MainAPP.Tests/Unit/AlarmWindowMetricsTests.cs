using Kanban.Analysis;
using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class AlarmWindowMetricsTests
{
    [Fact]
    public void Build_SplitsTodayYesterdayAndWindow()
    {
        var to = new DateTime(2026, 8, 8, 10, 0, 0);
        var from = to.AddHours(-4);
        var events = new[]
        {
            Ev(1, AlarmEventType.Triggered, to.Date.AddDays(-1).AddHours(8), "A"),
            Ev(2, AlarmEventType.Triggered, to.Date.AddHours(1), "B"),
            Ev(3, AlarmEventType.Recovered, to.Date.AddHours(1).AddMinutes(5), "B"),
            Ev(4, AlarmEventType.Triggered, from.AddMinutes(10), "C"),
        };

        var dto = AlarmWindowMetrics.Build(events, from, to, truncated: false);

        Assert.Equal(HistoryErrorCode.None, dto.ErrorCode);
        Assert.Equal(1, dto.WindowTriggered);
        Assert.Equal(0, dto.WindowRecovered);
        Assert.Equal(2, dto.TodayTriggered);
        Assert.Equal(1, dto.TodayRecovered);
        Assert.Equal(1, dto.YesterdayTriggered);
        Assert.Equal("C", Assert.Single(dto.Top).AlarmName);
        Assert.Equal("C", Assert.Single(dto.Recent).AlarmName);
    }

    [Fact]
    public void BuildTop_ConsumesEachRecoverOnce()
    {
        var t = new DateTime(2026, 8, 8, 8, 0, 0);
        var events = new[]
        {
            Ev(1, AlarmEventType.Triggered, t, "A"),
            Ev(2, AlarmEventType.Triggered, t.AddMinutes(1), "A"),
            Ev(3, AlarmEventType.Recovered, t.AddMinutes(3), "A"),
        };

        var top = Assert.Single(AlarmWindowMetrics.BuildTop(events));
        Assert.Equal(2, top.TriggerCount);
        Assert.Equal(3, top.TotalDurationMinutes, 3);
    }

    [Fact]
    public void BuildShiftSummaries_AssignsAlarmsOnceAcrossRepeatedShiftNames()
    {
        var start = new DateTime(2026, 8, 8, 8, 0, 0);
        var logs = new List<ProductionLogDto>
        {
            Log(start, 10, "早班"),
            Log(start.AddMinutes(10), 20, "早班"),
            Log(start.AddHours(12), 1, "夜班"),
            Log(start.AddHours(24), 2, "早班"),
            Log(start.AddHours(24).AddMinutes(10), 5, "早班"),
        };
        var alarms = new List<AlarmEventRecordDto>
        {
            Ev(1, AlarmEventType.Triggered, start.AddMinutes(-1), "A"),
            Ev(2, AlarmEventType.Triggered, start.AddMinutes(11), "A"),
            Ev(3, AlarmEventType.Triggered, start.AddHours(12), "B") with { ShiftName = "夜班" },
            Ev(4, AlarmEventType.Recovered, start.AddHours(24), "A"),
            Ev(5, AlarmEventType.Triggered, start.AddHours(24), "A"),
            Ev(6, AlarmEventType.Triggered, start.AddHours(24).AddMinutes(11), "A"),
        };

        var rows = ReviewWindowMetrics.BuildShiftSummaries(logs, alarms);

        Assert.Equal(new[] { "早班", "夜班", "早班" }, rows.Select(r => r.ShiftName));
        Assert.Equal(new[] { 2, 1, 2 }, rows.Select(r => r.AlarmCount));
        Assert.Equal(5, rows.Sum(r => r.AlarmCount));
    }

    [Fact]
    public void BuildShiftSummaries_SameNameResetDoesNotDuplicateAlarm()
    {
        var start = new DateTime(2026, 8, 8, 8, 0, 0);
        var logs = new List<ProductionLogDto>
        {
            Log(start, 10, "早班"),
            Log(start.AddMinutes(10), 20, "早班"),
            Log(start.AddMinutes(20), 0, "早班"),
            Log(start.AddMinutes(30), 3, "早班"),
        };
        var alarms = new List<AlarmEventRecordDto>
        {
            Ev(1, AlarmEventType.Triggered, start.AddMinutes(19), "A"),
            Ev(2, AlarmEventType.Triggered, start.AddMinutes(20), "A"),
            Ev(3, AlarmEventType.Recovered, start.AddMinutes(20), "A"),
        };

        var rows = ReviewWindowMetrics.BuildShiftSummaries(logs, alarms);

        Assert.Equal(new[] { 1, 1 }, rows.Select(r => r.AlarmCount));
    }

    private static ProductionLogDto Log(DateTime time, int ok, string shift) => new()
    {
        DeviceId = "dev1",
        DeviceName = "设备1",
        Timestamp = time,
        OkProduction = ok,
        ShiftName = shift,
    };

    private static AlarmEventRecordDto Ev(int id, AlarmEventType type, DateTime time, string name) => new()
    {
        Id = id,
        DeviceId = "dev1",
        DeviceName = "设备1",
        AlarmId = name,
        AlarmName = name,
        PlcAddress = "D0",
        EventType = type,
        EventTime = time,
        ShiftName = "早班",
    };
}
