using HslCommunication.ModBus;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslModbusTcpDriver 端到端仿真测试。
///
/// 使用 HslCommunication 自带的 <see cref="ModbusTcpServer"/> 作为虚拟 Modbus TCP 从站，
/// 让真实的 <see cref="HslModbusTcpDriver"/> 通过 TCP 回环连接，验证 Modbus 协议下的
/// 连接 / 读写 / 批量读 / 断开 / 配置切换 / 不可达失败 等真实行为。
///
/// 通用契约（连接/写读往返/批读/断线/配置切换/释放/能力一致性）由
/// <see cref="PlcDriverContractTestBase{TDriver, TServer}"/> 统一执行，
/// 本类只保留品牌特有断言（Modbus 能力轮廓）。
/// 地址约定：Modbus 使用纯数字字符串地址（如 "100"），驱动直接透传给 HslCommunication 客户端。
/// 寄存器（holding register）用于 Int32/UInt16（Int32 占 2 个寄存器，stride=2），线圈用于 Bool。
/// </summary>
[Trait("Category","Simulation")]
[Trait("Speed","Slow")]
[Trait("Requires","Network")]
public sealed class HslModbusTcpDriverSimulationTests : PlcDriverContractTestBase<ModbusTcpServer>
{
    protected override PlcBrand Brand => PlcBrand.ModbusTcp;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslModbusTcpDriver(config, NullLogger<HslModbusTcpDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.ModbusTcp,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        ModbusUnitId = 1,
    };

    protected override string Int32Address => "100";
    protected override string UInt16Address => "200";
    protected override string BoolAddress => "10";
    protected override int Int32BatchBaseOffset => 300;
    protected override int Int32AddressStride => 2;
    protected override string Int32BatchAddressTemplate => "{0}";
    protected override string BoolBatchStartAddress => "20";
    protected override string FormatBoolBitAddress(int index) => (20 + index).ToString();
    protected override string StringAddress => "500";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslModbusTcpDriver(config, NullLogger<HslModbusTcpDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    // ──────────── 品牌特有：能力轮廓 ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedModbusProfile()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)62, caps.MaxInt32Length);
        Assert.Equal(2, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
        Assert.Equal((ushort)2000, caps.MaxBoolLength);
        Assert.Equal(1, caps.BoolAddressStride);
    }
}
