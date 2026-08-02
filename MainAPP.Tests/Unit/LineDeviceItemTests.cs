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
}
