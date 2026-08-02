using HslCommunication.ModBus;
using HslCommunication.Profinet.Siemens;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslModbusAdapterRouteSimulationTests : HslPlcSimulationTestBase<ModbusTcpServer>
{
    [Fact]
    public void SharedRouterAndAdapter_TranslateHoldingRegisterAddress()
    {
        var settings = new AppSettings();
        settings.PlcConfig = new PlcConfig
        {
            Brand = PlcBrand.ModbusTcp,
            IpAddress = "127.0.0.1",
            Port = Port,
            TimeoutMs = 3000,
            ModbusUnitId = 1,
        };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var profileProvider = new PlcRuntimeProfileProvider(settings, codecResolver);
        using var router = new SharedPlcDriverRouter(
            new HslSharedPlcDriverFactory(NullLoggerFactory.Instance), settings, profileProvider);
        var adapter = new PlcDeviceAdapter(router, profileProvider, codecResolver);

        Assert.True(router.Connect().IsSuccess);
        Assert.True(adapter.WriteInt32("HR100", 778899).IsSuccess);

        var result = adapter.ReadInt32("HR100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(778899, result.Content);
    }
}

[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslSiemensAdapterRouteSimulationTests : HslPlcSimulationTestBase<SiemensS7Server>
{
    [Fact]
    public void SharedRouterAndAdapter_TranslateSiemensAliasAddress()
    {
        var settings = new AppSettings();
        settings.PlcConfig = new PlcConfig
        {
            Brand = PlcBrand.Siemens,
            IpAddress = "127.0.0.1",
            Port = Port,
            TimeoutMs = 3000,
            SiemensModel = "S1200",
            SiemensRack = 0,
            SiemensSlot = 1,
        };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var profileProvider = new PlcRuntimeProfileProvider(settings, codecResolver);
        using var router = new SharedPlcDriverRouter(
            new HslSharedPlcDriverFactory(NullLoggerFactory.Instance), settings, profileProvider);
        var adapter = new PlcDeviceAdapter(router, profileProvider, codecResolver);

        Assert.True(router.Connect().IsSuccess);
        Assert.True(adapter.WriteInt32("MD100", 778899).IsSuccess);

        var result = adapter.ReadInt32("MD100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(778899, result.Content);
    }
}
