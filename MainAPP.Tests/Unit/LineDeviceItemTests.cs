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

        // 1020s -> "17m 0s"
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

    // ═══════════════ 节拍对比 / 班次进度（2026-08-11 三项增强） ═══════════════

private static AppSettings ShiftsAppSettings()
{
    var appSettings = new AppSettings();
    appSettings.Shifts.Add(new ShiftConfig
    {
        Name = "白班",
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(20, 0, 0),
    });
    // 夜班跨天（20:00-08:00）：保证任意时刻都有当前班次，测试不依赖运行时间
    appSettings.Shifts.Add(new ShiftConfig
    {
        Name = "夜班",
        StartTime = new TimeSpan(20, 0, 0),
        EndTime = new TimeSpan(8, 0, 0),
    });
    return appSettings;
}

[Fact]
public void CycleText_ShowsRealOverTarget_AndSlowFlag()
{
    var device = new Device { TargetCycle = 400 }; // 400 件/小时 → 理论节拍 9.0s
    var runtime = new DeviceRuntime(device);
    runtime.RunTime = 3600;          // 1 小时
    runtime.TotalOkProduction = 200; // 产量 200 → 性能率 200/(400×1)=0.5 → 真实节拍 18.0s
    runtime.TotalNgProduction = 0;
    var item = new LineDeviceItem(device, runtime);

    Assert.Equal(9.0, item.TargetCycleSec, 3);
    Assert.Equal(18.0, item.RealCycleSec, 3);
    Assert.Equal("18.0/9.0s", item.CycleText);
    Assert.True(item.IsCycleSlow);
}

[Fact]
public void ShiftProgress_ComputesAgainstShiftCapacity()
{
    var device = new Device { TargetCycle = 400 }; // 件/小时
    var runtime = new DeviceRuntime(device);
    runtime.TotalOkProduction = 2400;
    var item = new LineDeviceItem(device, runtime, ShiftsAppSettings());

    Assert.Equal(4800, item.ShiftTargetQuantity); // 400 × 12h
    Assert.True(item.HasShiftTarget);
    Assert.Equal(0.5, item.ShiftProgressRatio, 3);
    // ShiftProgressText 不含单位（单一“件”单位由 ShiftProgressFullText 拼接），对齐当前实现
    Assert.Equal("2,400 / 4,800", item.ShiftProgressText);
}

[Fact]
public void ShiftProgress_NoShiftConfig_Hidden()
{
    var device = new Device { TargetCycle = 400 };
    var runtime = new DeviceRuntime(device);
    var item = new LineDeviceItem(device, runtime); // appSettings = null

    Assert.False(item.HasShiftTarget);
    Assert.Equal(string.Empty, item.ShiftProgressText);
}
}
