using HslCommunication.ModBus;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 汇川驱动端到端仿真。H5U 的 D/M 经 InovanceTcpNet 译成 Modbus 地址后，
/// 由 ModbusTcpServer 承接写读往返。
/// </summary>
[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslInovanceTcpDriverSimulationTests : PlcDriverContractTestBase<ModbusTcpServer>
{
    protected override PlcBrand Brand => PlcBrand.Inovance;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslInovanceTcpDriver(config, NullLogger<HslInovanceTcpDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Inovance,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        InovanceSeries = InovancePlcSeries.H5U,
        InovanceStation = 1,
        InovanceDataFormat = PlcDataFormat.CDAB,
        InovanceBatchInt32Limit = 60,
    };

    protected override string Int32Address => "D100";
    protected override string UInt16Address => "D200";
    protected override string BoolAddress => "M10";
    protected override int Int32BatchBaseOffset => 300;
    protected override int Int32AddressStride => 2;
    protected override string Int32BatchAddressTemplate => "D{0}";
    protected override string BoolBatchStartAddress => "M20";
    protected override string FormatBoolBitAddress(int index) => $"M{20 + index}";
    protected override string StringAddress => "D500";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslInovanceTcpDriver(config, NullLogger<HslInovanceTcpDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedInovanceProfile()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)60, caps.MaxInt32Length);
        Assert.Equal(2, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
    }
}
