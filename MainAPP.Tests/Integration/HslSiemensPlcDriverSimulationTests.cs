using HslCommunication.Profinet.Siemens;
using MainAPP.Models;
using MainAPP.Services;
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
/// 注意：<see cref="SiemensS7Server"/> 文档标注“仅限商业授权用户使用”；HslCommunication 对未授权的
/// 网络通信提供 24 小时宽限期，开发/测试环境通常可在宽限期内运行。若 CI 因授权失败报错，
/// 可通过 <c>dotnet test --filter FullyQualifiedName!~HslSiemensPlcDriverSimulation</c> 临时跳过，
/// 或调用 <c>Authorization.SetAuthorizationCode</c> 注入授权码。
/// </summary>
[Trait("Category","Simulation")]
[Trait("Speed","Slow")]
[Trait("Requires","Network")]
public sealed class HslSiemensPlcDriverSimulationTests : HslPlcSimulationTestBase<SiemensS7Server>
{
    /// <summary>创建指向 127.0.0.1:Port 的 Siemens 驱动（S7-1200, Rack=0, Slot=1, 超时 3000ms）。</summary>
    private HslSiemensPlcDriver CreateDriver() => new(
        BuildLiveConfig(),
        NullLogger<HslSiemensPlcDriver>.Instance);

    private PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.Siemens,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        SiemensModel = "S1200",
        SiemensRack = 0,
        SiemensSlot = 1,
    };

    // ──────────── BatchReadCapabilities ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedSiemensProfile()
    {
        using var driver = CreateDriver();

        var caps = driver.BatchReadCapabilities;

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

    // ──────────── Connect / Disconnect ────────────

    [Fact]
    public void Connect_ToLiveServer_Succeeds()
    {
        using var driver = CreateDriver();

        var result = driver.Connect();

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void Disconnect_AfterConnect_Succeeds()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var result = driver.Disconnect();

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void Connect_ToUnreachableServer_ReturnsFailure()
    {
        // 指向一个没有服务器监听的端口 → 连接被拒绝
        var deadPort = AllocateFreePort();
        var config = BuildLiveConfig();
        config.Port = deadPort;
        using var driver = new HslSiemensPlcDriver(config, NullLogger<HslSiemensPlcDriver>.Instance);

        var result = driver.Connect();

        Assert.False(result.IsSuccess);
    }

    // ──────────── Read ────────────

    [Fact]
    public void ReadInt32_ReturnsPresetValue()
    {
        const int expected = 123456;
        PresetInt32("M100", expected);
        using var driver = CreateDriver();
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
        using var driver = CreateDriver();
        driver.Connect();

        var result = driver.ReadUInt16("M200");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void ReadBool_ReturnsPresetValue()
    {
        PresetBool("M10", true);
        using var driver = CreateDriver();
        driver.Connect();

        var result = driver.ReadBool("M10");

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Content);
    }

    [Fact]
    public void ReadInt32_Fails_AfterNetworkDisconnect()
    {
        // HslCommunication 客户端在 ConnectClose() 后会自动重连，
        // 因此不能用 driver.Disconnect() 测试断线——必须通过 CutConnection 切断网络。
        PresetInt32("M100", 42);
        using var driver = CreateDriver();
        driver.Connect();

        CutConnection();  // 切断 TCP 代理，模拟网络断线

        var result = driver.ReadInt32("M100");

        Assert.False(result.IsSuccess);
    }

    // ──────────── Batch Read ────────────

    [Fact]
    public void ReadInt32Batch_ReturnsPresetValues()
    {
        var expected = new[] { 100, 200, 300 };
        PresetInt32Array("M300", expected);
        using var driver = CreateDriver();
        driver.Connect();

        var result = driver.ReadInt32Batch("M300", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void WriteAndReadBoolBatch_RoundTrips()
    {
        // SiemensS7Server 不支持 Write(address, bool[])，通过驱动逐位写入再批量读。
        // ReadBoolBatch("M20", 4) 读取 M20.0~M20.3 共 4 个连续位。
        using var driver = CreateDriver();
        driver.Connect();

        var expected = new[] { true, false, true, false };
        for (var i = 0; i < expected.Length; i++)
        {
            var w = driver.WriteBool($"M20.{i}", expected[i]);
            Assert.True(w.IsSuccess, w.Message);
        }

        var result = driver.ReadBoolBatch("M20", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    // ──────────── Write（驱动写 → 驱动读回，避免字节序差异） ────────────

    [Fact]
    public void WriteInt32_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteInt32("M100", 778899);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadInt32("M100");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(778899, read.Content);
    }

    [Fact]
    public void WriteUInt16_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteUInt16("M200", 12345);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadUInt16("M200");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)12345, read.Content);
    }

    [Fact]
    public void WriteBool_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteBool("M10", true);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadBool("M10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.True(read.Content);
    }

    // ──────────── Configure ────────────

    [Fact]
    public void Configure_ToNewEndpoint_RecreatesClientAndConnectsToLiveServer()
    {
        // 初始指向无服务器端口，连接失败
        var deadPort = AllocateFreePort();
        var deadConfig = BuildLiveConfig();
        deadConfig.Port = deadPort;
        using var driver = new HslSiemensPlcDriver(deadConfig, NullLogger<HslSiemensPlcDriver>.Instance);

        Assert.False(driver.Connect().IsSuccess);

        // 切换到运行中的虚拟服务器，连接应成功
        driver.Configure("127.0.0.1", Port);

        Assert.True(driver.Connect().IsSuccess);
    }

    // ──────────── Dispose ────────────

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var driver = CreateDriver();
        driver.Connect();

        driver.Dispose();
        driver.Dispose();  // 重复释放不应抛异常
    }
}
