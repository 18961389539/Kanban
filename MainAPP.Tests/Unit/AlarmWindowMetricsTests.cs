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
