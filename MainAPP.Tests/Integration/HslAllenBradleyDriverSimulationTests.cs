using HslCommunication.Profinet.AllenBradley;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// 罗克韦尔驱动端到端仿真。AllenBradleyServer 按标签名存值，
/// 验证无连接 CIP 客户端的写读往返。采集规划关闭跨标签批量，驱动本身仍保留数组批量接口。
/// </summary>
[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslAllenBradleyDriverSimulationTests : PlcDriverContractTestBase<AllenBradleyServer>
{
    public HslAllenBradleyDriverSimulationTests()
    {
        // CreateTagWithWrite 只对 BOOL 生效。DINT / REAL / STRING / 数组必须先建标签，客户端写入才会成功。
        Server.AddTagValue(Int32Address, 0);
        Server.AddTagValue(UInt16Address, (ushort)0);
        Server.AddTagValue(BoolAddress, false);
        Server.AddTagValue("Counts", new int[8]);
        Server.AddTagValue("Flags", new bool[8]);
        Server.AddTagValue(StringAddress, string.Empty, 20);
    }

    protected override AllenBradleyServer CreateServer() => new() { CreateTagWithWrite = true };

    protected override PlcBrand Brand => PlcBrand.AllenBradley;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslAllenBradleyDriver(config, NullLogger<HslAllenBradleyDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.AllenBradley,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        AllenBradleySlot = 0,
        AllenBradleyUseConnectedCip = false,
    };

    protected override string Int32Address => "Count";
    protected override string UInt16Address => "Word1";
    protected override string BoolAddress => "Flag";
    protected override int Int32BatchBaseOffset => 0;
    protected override int Int32AddressStride => 1;
    protected override string Int32BatchAddressTemplate => "Counts[{0}]";
    protected override string BoolBatchStartAddress => "Flags[0]";
    protected override string FormatBoolBitAddress(int index) => $"Flags[{index}]";
    protected override string StringAddress => "Text1";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslAllenBradleyDriver(config, NullLogger<HslAllenBradleyDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    [Fact]
    public void BatchReadCapabilities_DisablesPlannerBatch()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.False(caps.SupportsInt32);
        Assert.False(caps.SupportsBool);
        Assert.Equal(1, caps.Int32AddressStride);
    }
}
