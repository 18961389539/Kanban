using HslCommunication.Profinet.Melsec;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslKeyenceMcDriver 端到端仿真测试。
///
/// KeyenceMcNet 使用 MC 协议（QnA 兼容 3E 帧，Binary），与三菱 MelsecMcNet 同属 MC 协议族，
/// 因此复用 HslCommunication 自带的 <see cref="MelsecMcServer"/> 作为虚拟 MC 服务器。
/// 让真实的 <see cref="HslKeyenceMcDriver"/> 通过 TCP 回环连接，验证 MC 协议下的
/// 连接 / 读写 / 批量读 / 断开 / 配置切换 / 不可达失败 等真实行为。
///
/// 通用契约（连接/写读往返/批读/断线/配置切换/释放/能力一致性）由
/// <see cref="PlcDriverContractTestBase{TDriver, TServer}"/> 统一执行，
/// 本类只保留品牌特有断言（服务器预置读回、Keyence 能力轮廓）。
/// 地址约定：KeyenceMcNet 同时支持原生格式（DM100/MR100）和三菱兼容格式（D100/M100）。
/// 本测试使用三菱兼容格式，便于复用 MelsecMcServer 预置数据；原生格式的解析已由
/// PlcBrandCodecTests 覆盖，本测试聚焦端到端读写往返。
/// </summary>
[Trait("Category", "Simulation")]
[Trait("Speed", "Slow")]
[Trait("Requires", "Network")]
public sealed class HslKeyenceMcDriverSimulationTests : PlcDriverContractTestBase<MelsecMcServer>
{
    /// <summary>
    /// MelsecMcServer 仅有 <c>#ctor(bool isBinary)</c> 公共构造，重写工厂以传 <c>true</c>
    /// 使用二进制 MC 帧格式（KeyenceMcNet 默认协议）。
    /// </summary>
    protected override MelsecMcServer CreateServer() => new(true);

    protected override PlcBrand Brand => PlcBrand.Keyence;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslKeyenceMcDriver(config, NullLogger<HslKeyenceMcDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Keyence,
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
        using var driver = new HslKeyenceMcDriver(config, NullLogger<HslKeyenceMcDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    // ──────────── 品牌特有：能力轮廓 ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedKeyenceProfile()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)480, caps.MaxInt32Length);
        Assert.Equal(2, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
        Assert.Equal((ushort)2000, caps.MaxBoolLength);
        Assert.Equal(1, caps.BoolAddressStride);
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
}
