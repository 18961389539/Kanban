using System.Linq;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class SampleDeviceBuilderTests
{
    [Fact]
    public void BuildSampleDevices_ReturnsTwentyDevices()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        Assert.Equal(20, devices.Count);
    }

    [Fact]
    public void BuildSampleDevices_AllDevicesHaveRequiredAddressesAndPositiveCycle()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        foreach (var d in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.OkCountAddress), $"{d.Name} OkCountAddress");
            Assert.False(string.IsNullOrWhiteSpace(d.NgCountAddress), $"{d.Name} NgCountAddress");
            Assert.False(string.IsNullOrWhiteSpace(d.StatusCountAddress), $"{d.Name} StatusCountAddress");
            Assert.False(string.IsNullOrWhiteSpace(d.ProductionResetAddress), $"{d.Name} ProductionResetAddress");
            Assert.True(d.TargetCycle > 0, $"{d.Name} TargetCycle");
        }
    }

    [Fact]
    public void BuildSampleDevices_EachDeviceHasAlarmsDefectsCounterAlarms()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        foreach (var d in devices)
        {
            Assert.NotEmpty(d.Alarms);
            Assert.NotEmpty(d.Defects);
            Assert.NotEmpty(d.CounterAlarms);
        }
    }

    [Fact]
    public void BuildSampleDevices_NoCrossDeviceAddressConflict()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        var conflicts = DeviceConfigValidator.CollectCrossDeviceConflicts(devices);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void BuildSampleDevices_NoStructuralValidationErrors()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        // 样本数据允许计数报警阈值=0（仅记录不触发），不应产生任何结构性校验错误
        var errors = DeviceConfigValidator.CollectValidationErrors(devices);
        Assert.Empty(errors);
    }
}
