using System;
using Kanban.Collector.Core.Models;
using Kanban.Contracts.Metrics;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// Contracts 班次窗解析必须与 Core ShiftConfig.ResolveRange 同口径（含 24:00 跨天）。
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class ShiftWindowResolverTests
{
    private static readonly DateTime Day = new(2026, 7, 23, 0, 0, 0);

    private static DateTime At(int h, int m = 0) => Day.AddHours(h).AddMinutes(m);

    [Fact]
    public void ResolveRange_MatchesCore_DayShift_SameDay()
    {
        var core = new ShiftConfig { StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.Equal(core.ResolveRange(At(10, 0)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(10, 0)));
    }

    [Fact]
    public void ResolveRange_MatchesCore_DayShift_BeforeStart()
    {
        var core = new ShiftConfig { StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.Equal(core.ResolveRange(At(7, 0)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(7, 0)));
    }

    [Fact]
    public void ResolveRange_MatchesCore_DayShift_AfterEnd()
    {
        var core = new ShiftConfig { StartTime = new(8, 0, 0), EndTime = new(20, 0, 0) };
        Assert.Equal(core.ResolveRange(At(21, 0)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(21, 0)));
    }

    [Fact]
    public void ResolveRange_MatchesCore_NightShift_Evening()
    {
        var core = new ShiftConfig { StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.Equal(core.ResolveRange(At(23, 0)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(23, 0)));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(12, 0)]
    public void ResolveRange_MatchesCore_NightShift_MorningOrGap(int h, int m)
    {
        var core = new ShiftConfig { StartTime = new(20, 0, 0), EndTime = new(8, 0, 0) };
        Assert.Equal(core.ResolveRange(At(h, m)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(h, m)));
    }

    [Fact]
    public void ResolveRange_MatchesCore_EndTime24_Evening()
    {
        var core = new ShiftConfig { StartTime = new(16, 0, 0), EndTime = TimeSpan.FromHours(24) };
        Assert.Equal(core.ResolveRange(At(23, 0)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(23, 0)));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(12, 0)]
    public void ResolveRange_MatchesCore_EndTime24_MorningOrGap(int h, int m)
    {
        var core = new ShiftConfig { StartTime = new(16, 0, 0), EndTime = TimeSpan.FromHours(24) };
        Assert.Equal(core.ResolveRange(At(h, m)), ShiftWindowResolver.ResolveRange(core.StartTime, core.EndTime, At(h, m)));
    }
}
