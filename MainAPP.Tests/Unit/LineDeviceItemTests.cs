using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class LineDeviceItemTests
{
    [Fact]
    public void DowntimeFormatted_Combines_Alarm_And_Paused()
    {
        var device = new Device { Name = "D1" };
        var runtime = new DeviceRuntime(device);
        runtime.AlarmTime = 300;   // 5m
        runtime.PausedTime = 720;  // 12m
        var item = new LineDeviceItem(device, runtime);

        Assert.Equal("17m 0s", item.DowntimeFormatted);
    }

    [Fact]
    public void DowntimeFormatted_Updates_When_AlarmTime_Changes()
    {
        var device = new Device();
        var runtime = new DeviceRuntime(device);
        var item = new LineDeviceItem(device, runtime);

        runtime.AlarmTime = 60; // 1m
        Assert.Equal("1m 0s", item.DowntimeFormatted);

        runtime.PausedTime = 120; // +2m -> 3m
        Assert.Equal("3m 0s", item.DowntimeFormatted);
    }

    [Fact]
    public void ActualCycleSec_Zero_When_TargetCycle_Is_Zero()
    {
        var device = new Device { TargetCycle = 0 };
        var runtime = new DeviceRuntime(device);
        var item = new LineDeviceItem(device, runtime);

        Assert.Equal(0, item.ActualCycleSec);
    }

    private static AppSettings ShiftsAppSettings()
    {
        var appSettings = new AppSettings();
        appSettings.Shifts.Add(new ShiftConfig
        {
            Name = "白班",
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(20, 0, 0),
        });
        appSettings.Shifts.Add(new ShiftConfig
        {
            Name = "夜班",
            StartTime = new TimeSpan(20, 0, 0),
            EndTime = new TimeSpan(8, 0, 0),
        });
        return appSettings;
    }

    [Fact]
    public void CycleText_ShowsAverageOverTarget_AndSlowFlag()
    {
        var device = new Device { TargetCycle = 400 }; // 400 件/小时 → 目标 9.0s
        var runtime = new DeviceRuntime(device);
        runtime.RunTime = 3600;
        runtime.TotalOkProduction = 200;
        runtime.TotalNgProduction = 0;
        var item = new LineDeviceItem(device, runtime);

        Assert.Equal(9.0, item.TargetCycleSec, 3);
        Assert.Equal(18.0, item.RealCycleSec, 3);
        Assert.Equal(18.0, item.ActualCycleSec, 3);
        Assert.Equal("18.0/9.0s", item.CycleText);
        Assert.True(item.IsCycleSlow);
    }

    [Fact]
    public void CycleText_Overspeed_FasterThanTarget_NotClamped()
    {
        var device = new Device { TargetCycle = 400 };
        var runtime = new DeviceRuntime(device);
        runtime.RunTime = 3600;
        runtime.TotalOkProduction = 800;
        runtime.TotalNgProduction = 0;
        var item = new LineDeviceItem(device, runtime);

        Assert.Equal(4.5, item.RealCycleSec, 3);
        Assert.Equal("4.5/9.0s", item.CycleText);
        Assert.False(item.IsCycleSlow);
    }

    [Fact]
    public void ShiftProgress_UsesElapsedExpectedNotFullShift()
    {
        var device = new Device { TargetCycle = 400 };
        var runtime = new DeviceRuntime(device);
        runtime.TotalOkProduction = 2400;
        var noon = new DateTime(2026, 9, 3, 14, 0, 0); // 白班已过 6h → 应产 2400
        var item = new LineDeviceItem(device, runtime, ShiftsAppSettings(), () => noon);

        Assert.Equal(4800, item.ShiftTargetQuantity);
        Assert.Equal(2400, item.ShiftExpectedQuantity);
        Assert.True(item.HasShiftTarget);
        Assert.Equal(1.0, item.ShiftProgressRatio, 3);
        Assert.Equal(1.0, item.ShiftProgressBarValue, 3);
        Assert.StartsWith("2,400 / 2,400", item.ShiftProgressText);
    }

    [Fact]
    public void ShiftProgress_Overachievement_BarCaps_TextShowsOver100()
    {
        var device = new Device { TargetCycle = 400 };
        var runtime = new DeviceRuntime(device);
        runtime.TotalOkProduction = 3600;
        var noon = new DateTime(2026, 9, 3, 14, 0, 0); // 应产 2400，实际 3600 → 150%
        var item = new LineDeviceItem(device, runtime, ShiftsAppSettings(), () => noon);

        Assert.Equal(1.5, item.ShiftProgressRatio, 3);
        Assert.Equal(1.0, item.ShiftProgressBarValue, 3);
        Assert.StartsWith("3,600 / 2,400", item.ShiftProgressText);
        Assert.Contains("150", item.ShiftProgressText);
    }

    [Fact]
    public void ShiftProgress_NoShiftConfig_Hidden()
    {
        var device = new Device { TargetCycle = 400 };
        var runtime = new DeviceRuntime(device);
        var item = new LineDeviceItem(device, runtime);

        Assert.False(item.HasShiftTarget);
        Assert.Equal(string.Empty, item.ShiftProgressText);
    }
}
