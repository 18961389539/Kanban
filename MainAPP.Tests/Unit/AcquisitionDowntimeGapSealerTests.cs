using System;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class AcquisitionDowntimeGapSealerTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 16, 0, 0);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    [Fact]
    public void TryResolve_LastRunningHoursAgo_SealsAtLastStatus()
    {
        var last = Status(DeviceStatus.Running, Now.AddHours(-3), "白班");

        Assert.True(AcquisitionDowntimeGapSealer.TryResolve(
            last, lastProduction: null, Now, MinGap,
            out var prev, out var at, out var shift));

        Assert.Equal((int)DeviceStatus.Running, prev);
        Assert.Equal(Now.AddHours(-3).AddTicks(1), at);
        Assert.Equal("白班", shift);
    }

    [Fact]
    public void TryResolve_LaterProductionSnapshot_SealsAtProductionTime()
    {
        var last = Status(DeviceStatus.Running, Now.AddHours(-8), "白班");
        var prod = Production(Now.AddHours(-3), (int)DeviceStatus.Running, "白班");

        Assert.True(AcquisitionDowntimeGapSealer.TryResolve(last, prod, Now, MinGap,
            out var prev, out var at, out _));

        Assert.Equal((int)DeviceStatus.Running, prev);
        Assert.Equal(Now.AddHours(-3).AddTicks(1), at);
    }

    [Fact]
    public void TryResolve_LastStatusAlreadyOffline_DoesNotSeal()
    {
        var last = Status(DeviceStatus.Offline, Now.AddHours(-3), "白班");

        Assert.False(AcquisitionDowntimeGapSealer.TryResolve(
            last, lastProduction: null, Now, MinGap, out _, out _, out _));
    }

    [Fact]
    public void TryResolve_CleanShutdownThenProductionStillOffline_DoesNotSeal()
    {
        var last = Status(DeviceStatus.Offline, Now.AddHours(-3), "白班");
        var prod = Production(Now.AddHours(-2), (int)DeviceStatus.Offline, "白班");

        Assert.False(AcquisitionDowntimeGapSealer.TryResolve(last, prod, Now, MinGap, out _, out _, out _));
    }

    [Fact]
    public void TryResolve_GapShorterThanMin_DoesNotSeal()
    {
        var last = Status(DeviceStatus.Running, Now.AddSeconds(-1), "白班");

        Assert.False(AcquisitionDowntimeGapSealer.TryResolve(
            last, lastProduction: null, Now, MinGap, out _, out _, out _));
    }

    [Fact]
    public void TryResolve_NoHistory_DoesNotSeal()
    {
        Assert.False(AcquisitionDowntimeGapSealer.TryResolve(
            lastStatus: null, lastProduction: null, Now, MinGap, out _, out _, out _));
    }

    [Fact]
    public void TryResolve_LastAlarm_SealsFromAlarm()
    {
        var last = Status(DeviceStatus.Alarm, Now.AddHours(-1), "夜班");

        Assert.True(AcquisitionDowntimeGapSealer.TryResolve(
            last, lastProduction: null, Now, MinGap,
            out var prev, out _, out var shift));

        Assert.Equal((int)DeviceStatus.Alarm, prev);
        Assert.Equal("夜班", shift);
    }

    [Fact]
    public void TryResolve_OnlyProductionHeartbeat_SealsUsingProductionStatusWord()
    {
        var prod = Production(Now.AddHours(-2), (int)DeviceStatus.Paused, "白班");

        Assert.True(AcquisitionDowntimeGapSealer.TryResolve(
            lastStatus: null, prod, Now, MinGap,
            out var prev, out var at, out var shift));

        Assert.Equal((int)DeviceStatus.Paused, prev);
        Assert.Equal(Now.AddHours(-2).AddTicks(1), at);
        Assert.Equal("白班", shift);
    }

    private static StatusTransitionRecord Status(DeviceStatus current, DateTime at, string shift) => new()
    {
        DeviceId = "d1",
        CurrentState = (int)current,
        EventTime = at,
        ShiftName = shift,
    };

    private static ProductionLog Production(DateTime at, int statusWord, string shift) => new()
    {
        DeviceId = "d1",
        Timestamp = at,
        StatusWord = statusWord,
        ShiftName = shift,
    };
}
