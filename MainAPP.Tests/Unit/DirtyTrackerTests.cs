using Kanban.Collector.Core.Models;
using MainAPP.ViewModels;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DirtyTrackerTests
{
    [Fact]
    public void RuntimeSampling_DoesNotMarkConfigurationDirty()
    {
        var dirtyCount = 0;
        var tracker = new DirtyTracker(() => dirtyCount++);
        var value = new DataSourceValue
        {
            Name = "温度",
            DataType = DataSourceValueType.Int32,
            PlcAddress = "D502",
        };
        var source = new DataSource { Name = "采集源" };
        source.Values.Add(value);
        var device = new Device { Name = "设备" };
        device.Sources.Add(source);
        tracker.AttachDevice(device);

        value.SetRuntimeValue(
            new DataSourceRuntimeValue(DataSourceValueType.Int32, Int32Value: 239),
            new DateTime(2026, 8, 19, 17, 0, 0));

        Assert.Equal(0, dirtyCount);

        value.Name = "温度修正";

        Assert.Equal(1, dirtyCount);
        tracker.DetachAll([device]);
    }
}
