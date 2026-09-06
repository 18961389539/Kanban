using System;
using System.Collections.Generic;
using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class HourByHourBoardBuilderTests
{
    private static readonly DateTime Day = new(2026, 9, 6, 0, 0, 0);

    private static DateTime At(int hour, int minute = 0) => Day.AddHours(hour).AddMinutes(minute);

    [Fact]
    public void Build_DayShift_MarksHitMissCurrentAndFuture()
    {
        var ok = new Dictionary<DateTime, int>
        {
            [At(8)] = 52,
            [At(9)] = 50,
            [At(10)] = 28,
            [At(11)] = 51,
            [At(12)] = 29,
            [At(13)] = 18,
        };

        var items = HourByHourBoardBuilder.Build(At(8), At(16), At(13, 22), 50, ok);

        Assert.Equal(8, items.Count);
        Assert.Equal("08–09", items[0].TimeLabel);
        Assert.Equal(HourBucketState.Hit, items[0].State);
        Assert.Equal(52, items[0].Actual);
        Assert.Equal(50, items[0].Plan);

        Assert.Equal(HourBucketState.Miss, items[2].State);
        Assert.Equal(28, items[2].Actual);
        Assert.Equal("10–11", items[2].TimeLabel);

        Assert.Equal(HourBucketState.Current, items[5].State);
        Assert.Equal("13–14", items[5].TimeLabel);
        Assert.Equal(18, items[5].Actual);
        Assert.Equal(18, items[5].Plan); // 50 * 22/60 rounded away from zero

        Assert.Equal(HourBucketState.Future, items[6].State);
        Assert.Null(items[6].Actual);
        Assert.Equal(50, items[6].Plan);
        Assert.Equal("—", items[6].ActualDisplay);
    }

    [Fact]
    public void Build_CurrentHour_ProratesPlanByElapsedMinutes()
    {
        var items = HourByHourBoardBuilder.Build(
            At(8), At(16), At(8, 22), 50, new Dictionary<DateTime, int> { [At(8)] = 10 });

        Assert.Equal(HourBucketState.Current, items[0].State);
        Assert.Equal(18, items[0].Plan);
        Assert.Equal(10, items[0].Actual);
    }

    [Fact]
    public void Build_NightShift_SpansMidnight()
    {
        var start = At(20);
        var end = Day.AddDays(1).AddHours(8);
        var now = At(23, 10);
        var items = HourByHourBoardBuilder.Build(start, end, now, 40, new Dictionary<DateTime, int>
        {
            [At(20)] = 40,
            [At(21)] = 22,
            [At(23)] = 5,
        });

        Assert.Equal(12, items.Count);
        Assert.Equal("20–21", items[0].TimeLabel);
        Assert.Equal(HourBucketState.Hit, items[0].State);
        Assert.Equal(HourBucketState.Miss, items[1].State);
        Assert.Equal(HourBucketState.Current, items[3].State);
        Assert.Equal("23–00", items[3].TimeLabel);
        Assert.Equal(7, items[3].Plan); // 40 * 10/60
        Assert.Equal(HourBucketState.Future, items[^1].State);
        Assert.Equal("07–08", items[^1].TimeLabel);
    }

    [Fact]
    public void Build_ZeroTarget_DoesNotMarkMiss()
    {
        var items = HourByHourBoardBuilder.Build(
            At(8), At(10), At(9, 30), 0, new Dictionary<DateTime, int> { [At(8)] = 0 });

        Assert.Equal(HourBucketState.Hit, items[0].State);
        Assert.Equal(0, items[0].Plan);
    }

    [Fact]
    public void Build_InvalidRange_ReturnsEmpty()
    {
        Assert.Empty(HourByHourBoardBuilder.Build(At(16), At(8), At(10), 50, new Dictionary<DateTime, int>()));
    }

    [Fact]
    public void RoundPlan_UsesAwayFromZero()
    {
        Assert.Equal(18, HourByHourBoardBuilder.RoundPlan(50, 22));
        Assert.Equal(25, HourByHourBoardBuilder.RoundPlan(50, 30));
        Assert.Equal(0, HourByHourBoardBuilder.RoundPlan(50, 0));
    }
}
