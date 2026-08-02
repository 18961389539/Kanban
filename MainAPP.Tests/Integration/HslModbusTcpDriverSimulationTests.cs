using HslCommunication.ModBus;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
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
/// 地址约定：Modbus 使用纯数字字符串地址（如 "100"），驱动直接透传给 HslCommunication 客户端。
/// 寄存器（holding register）用于 Int32/UInt16，线圈（coil）用于 Bool。
/// </summary>
[Trait("Category","Simulation")]
[Trait("Speed","Slow")]
[Trait("Requires","Network")]
public sealed class HslModbusTcpDriverSimulationTests : HslPlcSimulationTestBase<ModbusTcpServer>
{
    /// <summary>创建指向 127.0.0.1:Port 的 Modbus TCP 驱动（UnitId=1, 超时 3000ms）。</summary>
    private HslModbusTcpDriver CreateDriver() => new(
        BuildLiveConfig(),
        NullLogger<HslModbusTcpDriver>.Instance);

    private PlcConfig BuildLiveConfig() => new()
    {
        Brand = PlcBrand.ModbusTcp,
        IpAddress = "127.0.0.1",
        Port = Port,
        TimeoutMs = 3000,
        ModbusUnitId = 1,
    };

    // ──────────── BatchReadCapabilities ────────────

    [Fact]
    public void BatchReadCapabilities_MatchesExpectedModbusProfile()
    {
        using var driver = CreateDriver();

        var caps = driver.BatchReadCapabilities;

        Assert.True(caps.SupportsInt32);
        Assert.Equal((ushort)62, caps.MaxInt32Length);
        Assert.Equal(2, caps.Int32AddressStride);
        Assert.True(caps.SupportsBool);
        Assert.Equal((ushort)2000, caps.MaxBoolLength);
        Assert.Equal(1, caps.BoolAddressStride);
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
        var deadPort = AllocateFreePort();
        var config = BuildLiveConfig();
        config.Port = deadPort;
        using var driver = new HslModbusTcpDriver(config, NullLogger<HslModbusTcpDriver>.Instance);

        var result = driver.Connect();

        Assert.False(result.IsSuccess);
    }

    // ──────────── Write（驱动写 → 驱动读回，避免字节序差异） ────────────

    [Fact]
    public void WriteInt32_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteInt32("100", 778899);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadInt32("100");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(778899, read.Content);
    }

    [Fact]
    public void WriteUInt16_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteUInt16("200", 12345);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadUInt16("200");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)12345, read.Content);
    }

    [Fact]
    public void WriteBool_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateDriver();
        driver.Connect();

        var write = driver.WriteBool("10", true);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadBool("10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.True(read.Content);
    }

    // ──────────── Batch Read（先写后读，保证字节序一致） ────────────

    [Fact]
    public void WriteAndReadInt32Batch_RoundTrips()
    {
        using var driver = CreateDriver();
        driver.Connect();

        // Modbus 驱动未暴露批量写；用单值写连续寄存器后批量读
        var expected = new[] { 11, 22, 33 };
        for (var i = 0; i < expected.Length; i++)
        {
            // Int32 占 2 个寄存器；驱动批量读按 Int32AddressStride=2 推进寄存器地址
            var addr = 300 + i * 2;
            var w = driver.WriteInt32(addr.ToString(), expected[i]);
            Assert.True(w.IsSuccess, w.Message);
        }

        var result = driver.ReadInt32Batch("300", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void WriteAndReadBoolBatch_RoundTrips()
    {
        using var driver = CreateDriver();
        driver.Connect();

        // 先逐位写线圈，再批量读回
        var expected = new[] { true, false, true, false };
        for (var i = 0; i < expected.Length; i++)
        {
            var w = driver.WriteBool((20 + i).ToString(), expected[i]);
            Assert.True(w.IsSuccess, w.Message);
        }

        var result = driver.ReadBoolBatch("20", (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    // ──────────── 状态相关 ────────────

    [Fact]
    public void ReadInt32_Fails_AfterNetworkDisconnect()
    {
        // HslCommunication 客户端在 ConnectClose() 后会自动重连，
        // 因此不能用 driver.Disconnect() 测试断线——必须通过 CutConnection 切断网络。
        using var driver = CreateDriver();
        driver.Connect();
        driver.WriteInt32("100", 42);

        CutConnection();  // 切断 TCP 代理，模拟网络断线

        var result = driver.ReadInt32("100");

        Assert.False(result.IsSuccess);
    }

    // ──────────── Configure ────────────

    [Fact]
    public void Configure_ToNewEndpoint_RecreatesClientAndConnectsToLiveServer()
    {
        var deadPort = AllocateFreePort();
        var deadConfig = BuildLiveConfig();
        deadConfig.Port = deadPort;
        using var driver = new HslModbusTcpDriver(deadConfig, NullLogger<HslModbusTcpDriver>.Instance);

        Assert.False(driver.Connect().IsSuccess);

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
