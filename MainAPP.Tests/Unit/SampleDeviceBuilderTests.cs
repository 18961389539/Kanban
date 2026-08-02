using System.Linq;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
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
    public void BuildSampleDevices_EachDeviceHasAlarmsDefectsCountAlarms()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        foreach (var d in devices)
        {
            Assert.NotEmpty(d.Alarms);
            Assert.NotEmpty(d.Defects);
            Assert.NotEmpty(d.CountAlarms);
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
    public void BuildSampleDevices_NoStructuralValidationErrorsExceptZeroThresholdCounters()
    {
        var devices = SampleDeviceBuilder.BuildSampleDevices();
        var errors = DeviceConfigValidator.CollectValidationErrors(devices);
        // 样本数据允许计数报警阈值=0（仅记录不停机），过滤该类后不应有结构性错误
        var unexpected = errors
            .Where(e => !e.Message.Contains("阈值上限必须 > 0"))
            .ToList();
        Assert.Empty(unexpected);
    }
}
