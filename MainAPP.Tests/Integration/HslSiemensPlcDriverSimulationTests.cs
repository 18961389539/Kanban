using HslCommunication.Profinet.Siemens;
using Kanban.Core.Models;
using Kanban.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// HslSiemensPlcDriver 端到端仿真测试。
///
/// 使用 HslCommunication 自带的 <see cref="SiemensS7Server"/> 作为虚拟 S7-1200 PLC，
/// 让真实的 <see cref="HslSiemensPlcDriver"/> 通过 TCP 回环连接，验证 S7 协议下的
/// 连接 / 读写 / 批量读 / 断开 / 配置切换 / 不可达失败 等真实行为。
///
/// 通用契约（连接/写读往返/批读/断线/配置切换/释放/能力一致性）由
/// <see cref="PlcDriverContractTestBase{TDriver, TServer}"/> 统一执行，
/// 本类只保留品牌特有断言（服务器预置读回、批读上限随配置、S7 能力轮廓）。
/// 地址约定：Siemens M 存储区（Int32/UInt16/Bool），地址格式如 M100、M20.0；
/// M 区 Int32 按 4 字节寻址，故 Int32AddressStride=4。
///
/// 注意：<see cref="SiemensS7Server"/> 文档标注“仅限商业授权用户使用”；HslCommunication 对未授权的
/// 网络通信提供 24 小时宽限期，开发/测试环境通常可在宽限期内运行。若 CI 因授权失败报错，
/// 可通过 <c>dotnet test --filter FullyQualifiedName!~HslSiemensPlcDriverSimulation</c> 临时跳过，
/// 或调用 <c>Authorization.SetAuthorizationCode</c> 注入授权码。
/// </summary>
[Trait("Category","Simulation")]
[Trait("Speed","Slow")]
[Trait("Requires","Network")]
public sealed class HslSiemensPlcDriverSimulationTests : PlcDriverContractTestBase<SiemensS7Server>
{
    protected override PlcBrand Brand => PlcBrand.Siemens;
    protected override IPlcDriver CreateDriver(PlcConfig config) =>
        new HslSiemensPlcDriver(config, NullLogger<HslSiemensPlcDriver>.Instance);

    protected override PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Siemens,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        SiemensModel = "S1200",
        SiemensRack = 0,
        SiemensSlot = 1,
    };

    protected override string Int32Address => "M100";
    protected override string UInt16Address => "M200";
    protected override string BoolAddress => "M10";
    protected override int Int32BatchBaseOffset => 300;
    protected override int Int32AddressStride => 4;
    protected override string Int32BatchAddressTemplate => "M{0}";
    protected override string BoolBatchStartAddress => "M20";
    protected override string FormatBoolBitAddress(int index) => $"M20.{index}";
    protected override string StringAddress => "M500";
    protected override BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config)
    {
        using var driver = new HslSiemensPlcDriver(config, NullLogger<HslSiemensPlcDriver>.Instance);
        return driver.BatchReadCapabilities;
    }

    // ──────────── 品牌特有：批读能力轮廓 ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedSiemensProfile()
    {
        var caps = GetDriverBatchCapabilities(BuildLiveConfig());

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)55, caps.MaxInt32Length);
        Assert.Equal(4, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
        Assert.Equal((ushort)2000, caps.MaxBoolLength);
        Assert.Equal(1, caps.BoolAddressStride);
    }

    [Fact]
    public void BatchReadCapabilities_RespectsConfiguredBatchLimit()
    {
        var config = BuildLiveConfig();
        config.SiemensBatchInt32Limit = 20;
        using var driver = new HslSiemensPlcDriver(config, NullLogger<HslSiemensPlcDriver>.Instance);

        Assert.Equal((ushort)20, driver.BatchReadCapabilities.MaxInt32Length);
    }

    // ──────────── 品牌特有：服务器预置读回 ────────────

    [Fact]
    public void ReadInt32_ReturnsPresetValue()
    {
        const int expected = 123456;
        PresetInt32("M100", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadInt32("M100");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void ReadUInt16_ReturnsPresetValue()
    {
        const ushort expected = 47831;
        PresetUInt16("M200", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadUInt16("M200");

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
        PresetInt32Array("M300", expected);
        using var driver = CreateDriver(BuildLiveConfig());
        driver.Connect();

        var result = driver.ReadInt32Batch("M300", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }
}
