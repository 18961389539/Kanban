using HslCommunication.Profinet.Omron;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslOmronFinsDriver 端到端仿真测试。
///
/// 使用 HslCommunication 自带的 <see cref="OmronFinsServer"/> 作为虚拟欧姆龙 PLC，
/// 让真实的 <see cref="HslOmronFinsDriver"/> 通过 TCP 回环连接，验证 FINS TCP 协议下的
/// 连接 / 读写 / 批量读 / 断开 / 配置切换 / 不可达失败 等真实行为。
///
/// 通用契约（连接/写读往返/批读/断线/配置切换/释放/能力一致性）由
/// <see cref="PlcDriverContractTestBase{TDriver, TServer}"/> 统一执行，
/// 本类只保留品牌特有断言（服务器预置读回、ReadSplits 配置、FINS 能力轮廓）。
/// 地址约定：欧姆龙 FINS 使用 D 寄存器（Int32/UInt16）和 CIO 区域（Bool/字）。
/// 地址格式如 D100、CIO10。OmronFinsNet 内部按字（word）寻址，Int32 占 2 个字。
/// </summary>
[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslOmronFinsDriverSimulationTests : PlcDriverContractTestBase<OmronFinsServer>
{
    protected override PlcBrand Brand => PlcBrand.Omron;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslOmronFinsDriver(config, NullLogger<HslOmronFinsDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Omron,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        OmronReadSplits = 500,
    };

    protected override string Int32Address => "D100";
    protected override string UInt16Address => "D200";
    protected override string BoolAddress => "CIO10";
    protected override int Int32BatchBaseOffset => 300;
    protected override int Int32AddressStride => 2;
    protected override string Int32BatchAddressTemplate => "D{0}";
    protected override string BoolBatchStartAddress => "CIO10";
    protected override string FormatBoolBitAddress(int index) => $"CIO10.{index}";
    protected override string StringAddress => "D500";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslOmronFinsDriver(config, NullLogger<HslOmronFinsDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    // ──────────── 品牌特有：批读能力轮廓 ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedOmronProfile()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)250, caps.MaxInt32Length);  // OmronReadSplits/2 = 250
        Assert.Equal(2, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
        Assert.Equal((ushort)2000, caps.MaxBoolLength);
        Assert.Equal(1, caps.BoolAddressStride);
    }

    [Fact]
    public void BatchReadCapabilities_RespectsConfiguredReadSplits()
    {
        var config = BuildLiveConfig();
        config.OmronReadSplits = 100;
        using var driver = new HslOmronFinsDriver(config, NullLogger<HslOmronFinsDriver>.Instance);

        Assert.Equal((ushort)50, driver.BatchReadCapabilities.MaxInt32Length);
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
        PresetBool("CIO10", true);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadBool("CIO10");

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
}
