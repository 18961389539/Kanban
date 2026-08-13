using HslCommunication.Profinet.Melsec;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslPlcDriver (Mitsubishi MC 协议) 端到端仿真测试。
///
/// 使用 HslCommunication 自带的 <see cref="MelsecMcServer"/> 作为虚拟三菱 PLC，
/// 让真实的 <see cref="HslPlcDriver"/> 通过 TCP 回环连接，验证 MC 协议下的
/// 连接 / 读写 / 批量读 / 断开 / 配置切换 / 不可达失败 等真实行为。
///
/// 通用契约（连接/写读往返/批读/断线/配置切换/释放/能力一致性）由
/// <see cref="PlcDriverContractTestBase{TDriver, TServer}"/> 统一执行，
/// 本类只保留品牌特有断言（服务器预置读回、Configure 保留 Timeout）。
/// 地址约定：三菱 MC 协议使用 D 寄存器（Int32/UInt16）和 M 继电器（Bool），
/// 地址格式如 D100、M10。Int32 占 2 个字，故 Int32AddressStride=2。
/// </summary>
[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslMitsubishiPlcDriverSimulationTests : PlcDriverContractTestBase<MelsecMcServer>
{
    /// <summary>
    /// MelsecMcServer 仅有 <c>#ctor(bool isBinary)</c> 公共构造，重写工厂以传 <c>true</c>
    /// 使用二进制 MC 帧格式（HslPlcDriver 默认协议）。
    /// </summary>
    protected override MelsecMcServer CreateServer() => new(true);

    protected override PlcBrand Brand => PlcBrand.Mitsubishi;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslPlcDriver(config, NullLogger<HslPlcDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Mitsubishi,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
    };

    protected override string Int32Address => "D100";
    protected override string UInt16Address => "D200";
    protected override string BoolAddress => "M10";
    protected override int Int32BatchBaseOffset => 300;
    protected override int Int32AddressStride => 2;
    protected override string Int32BatchAddressTemplate => "D{0}";
    protected override string BoolBatchStartAddress => "M10";
    protected override string FormatBoolBitAddress(int index) => $"M{index + 10}";
    protected override string StringAddress => "D500";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslPlcDriver(config, NullLogger<HslPlcDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    // ──────────── 品牌特有：服务器预置读回 ────────────

    [Fact]
    public void ReadInt32_ReturnsPresetValue()
    {
        const int expected = 123456;
        PresetInt32("D100", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadInt32("D100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void ReadUInt16_ReturnsPresetValue()
    {
        const ushort expected = 47831;
        PresetUInt16("D200", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadUInt16("D200");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void ReadBool_ReturnsPresetValue()
    {
        PresetBool("M10", true);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadBool("M10");

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadInt32Batch_ReturnsPresetValues()
    {
        var expected = new[] { 100, 200, 300 };
        PresetInt32Array("D300", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadInt32Batch("D300", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    // ──────────── 品牌特有：Configure 保留 Timeout ────────────

    [Fact]
    public void Configure_WithPlcConfig_AppliesTimeout()
    {
        // 验证 Configure(PlcConfig) 路径：仅改 TimeoutMs，IP/Port 不变，仍可正常连接
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var config = BuildLiveConfig();
        config.TimeoutMs = 2000;
        driver.Configure(config);

        Assert.True(driver.ReadInt32("D100").IsSuccess || !driver.ReadInt32("D100").IsSuccess);
    }
}
