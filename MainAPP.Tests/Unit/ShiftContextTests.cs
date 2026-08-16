using System;
using System.Collections.ObjectModel;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class ShiftContextTests
{
    // 白班 08-20 + 夜班 20-08 覆盖全天，保证任何时刻都有当前班次（DetectChange 必能初始化）
    private static ObservableCollection<ShiftConfig> FullDayShifts()
        => new()
        {
            new ShiftConfig { Name = "白班", StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(20, 0, 0) },
            new ShiftConfig { Name = "夜班", StartTime = new TimeSpan(20, 0, 0), EndTime = new TimeSpan(8, 0, 0) },
        };

    [Fact]
    public void DetectChange_FirstCall_InitializesAndReturnsNull()
    {
        var ctx = new ShiftContext();
        var result = ctx.DetectChange(FullDayShifts());
        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(ctx.CurrentName));
    }

    [Fact]
    public void DetectChange_RepeatedCallSameShift_NoChangeSignal()
    {
        var ctx = new ShiftContext();
        ctx.DetectChange(FullDayShifts()); // 初始化
        var second = ctx.DetectChange(FullDayShifts());
        Assert.Null(second);
    }

    [Fact]
    public void SetAndGetLastShiftSummary_RoundTrips()
    {
        var ctx = new ShiftContext();
        ctx.SetLastShiftSummaryForTest("d1", 95, 5, "白班");
        var s = ctx.GetLastShiftSummary("d1");
        Assert.Equal(95, s.Ok);
        Assert.Equal(5, s.Ng);
        Assert.Equal("白班", s.ShiftName);
    }

    [Fact]
    public void GetLastShiftSummary_UnknownDevice_ReturnsEmpty()
    {
        var ctx = new ShiftContext();
        var s = ctx.GetLastShiftSummary("nope");
        Assert.Equal(0, s.Ok);
        Assert.Equal(0, s.Ng);
        Assert.Equal("", s.ShiftName);
    }

    [Fact]
    public void CacheLastShiftSummaries_UsesRuntimeTotals()
    {
        var ctx = new ShiftContext();
        var device = new Device { Id = "d1", Name = "设备1" };
        var rt = new DeviceRuntime(device) { TotalOkProduction = 120, TotalNgProduction = 7 };
        var devices = new ObservableCollection<Device> { device };

        ctx.CacheLastShiftSummaries(devices, d => d == device ? rt : null, "白班");
        var s = ctx.GetLastShiftSummary("d1");
        Assert.Equal(120, s.Ok);
        Assert.Equal(7, s.Ng);
        Assert.Equal("白班", s.ShiftName);
    }

    [Fact]
    public void CurrentStart_ComputesShiftStartForGivenTime()
    {
        var ctx = new ShiftContext();
        var shifts = FullDayShifts();
        var now = new DateTime(2026, 7, 26, 10, 30, 0); // 白班内
        var start = ctx.CurrentStart(now, shifts);
        Assert.Equal(new DateTime(2026, 7, 26, 8, 0, 0), start);
    }
}
