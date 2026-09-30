using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class DWordAddressBatchCollectorTests
{
    [Fact]
    public void Collect_IncludesOnlyEnabledValidDWordsAndDeduplicatesAddresses()
    {
        var device = new Device
        {
            OkCountAddress = " d100 ",
            NgCountAddress = "D100",
            StatusCountAddress = "M10",
        };
        device.Defects.Add(new Defect { PlcAddress = "D102" });
        device.Defects.Add(new Defect { PlcAddress = "X42" });
        device.CounterAlarms.Add(new CounterAlarm { PlcAddress = "d104" });
        device.CounterAlarms.Add(new CounterAlarm { PlcAddress = "D106", Enabled = false });

        var source = new DataSource { TriggerAddress = "D108" };
        source.Values.Add(new DataSourceValue { PlcAddress = "D110" });
        source.Values.Add(new DataSourceValue { PlcAddress = "D112", Enabled = false });
        source.Values.Add(new DataSourceValue { PlcAddress = "M11" });
        device.Sources.Add(source);

        var disabledSource = new DataSource { TriggerAddress = "D114", Enabled = false };
        disabledSource.Values.Add(new DataSourceValue { PlcAddress = "D116" });
        device.Sources.Add(disabledSource);

        var adapter = Substitute.For<IDeviceAdapter>();
        adapter.AddressCodec.Returns(new MitsubishiAddressCodec());

        var addresses = DWordAddressBatchCollector.Collect(device, adapter);

        Assert.Equal(5, addresses.Count);
        Assert.True(addresses.SetEquals(["D100", "D102", "D104", "D108", "D110"]));
    }

    [Fact]
    public void Collect_UsesAdapterCodecForAddressValidation()
    {
        var device = new Device
        {
            OkCountAddress = "MD100",
            NgCountAddress = "DB1.DBD4",
            StatusCountAddress = "D100",
        };
        var adapter = Substitute.For<IDeviceAdapter>();
        adapter.AddressCodec.Returns(new SiemensAddressCodec());

        var addresses = DWordAddressBatchCollector.Collect(device, adapter);

        Assert.Equal(2, addresses.Count);
        Assert.True(addresses.SetEquals(["MD100", "DB1.DBD4"]));
    }
}
