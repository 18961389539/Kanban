using HslCommunication.Core.Net;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// PLC 驱动统一契约测试基类：所有品牌必须通过同一组连接/读写/批读/断线/配置切换/释放断言，
/// 保证"宣称支持某品牌"意味着一致的协议行为质量，而不是各品牌测试各自为政。
///
/// 继承 <see cref="HslPlcSimulationTestBase{TServer}"/> 复用虚拟服务器 + TCP 代理基础设施；
/// 派生类只需提供驱动工厂（返回 <see cref="IPlcDriver"/> 接口，驱动可以是 internal 实现）、
/// 地址约定和品牌信息即可接入全部契约。
/// 读写往返采用"驱动写 → 驱动读回"，避免依赖服务器预置的字节序语义差异。
/// </summary>
/// <typeparam name="TServer">HslCommunication 虚拟服务器类型。</typeparam>
public abstract class PlcDriverContractTestBase<TServer> : HslPlcSimulationTestBase<TServer>
    where TServer : NetworkDataServerBase
{
    // ──────────── 派生类必须提供的品牌信息与地址约定 ────────────

    protected abstract PlcBrand Brand { get; }
    protected abstract IPlcDriver CreateDriver(PlcConfig config);
    protected abstract PlcConfig BuildLiveConfig();
    protected abstract string Int32Address { get; }
    protected abstract string UInt16Address { get; }
    protected abstract string BoolAddress { get; }
    /// <summary>Int32 批读起点偏移（如 300）；实际地址按 Int32AddressStride 推进。</summary>
    protected abstract int Int32BatchBaseOffset { get; }
    protected abstract int Int32AddressStride { get; }
    /// <summary>Int32 批读地址模板（{0}=数值偏移），如 "D{0}" / "M{0}" / "{0}"。</summary>
    protected abstract string Int32BatchAddressTemplate { get; }
    /// <summary>Bool 批读起始地址（不含位号），如 "M10" / "M20" / "CIO10" / "20"。</summary>
    protected abstract string BoolBatchStartAddress { get; }
    /// <summary>第 index（0 起）个 Bool 位地址，如 "M10" / "M20.{0}" / "CIO10.{0}" / "20"。</summary>
    protected abstract string FormatBoolBitAddress(int index);
    /// <summary>字符串读写地址（DWord 字区），如 "D500" / "M500" / "500" / "D500"。</summary>
    protected abstract string StringAddress { get; }
    /// <summary>读取指定配置下驱动实例的批读能力（BatchReadCapabilities 在具体驱动类型上）。</summary>
    protected abstract BatchReadCapabilities GetDriverBatchCapabilities(PlcConfig config);

    private IPlcDriver CreateLiveDriver() => CreateDriver(BuildLiveConfig());

    // ──────────── 契约：连接生命周期 ────────────

    [Fact]
    public void Connect_ToLiveServer_Succeeds()
    {
        using var driver = CreateLiveDriver();

        var result = driver.Connect();

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void Disconnect_AfterConnect_Succeeds()
    {
        using var driver = CreateLiveDriver();
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
        using var driver = CreateDriver(config);

        var result = driver.Connect();

        Assert.False(result.IsSuccess);
    }

    // ──────────── 契约：单值写读往返 ────────────

    [Fact]
    public void WriteInt32_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var write = driver.WriteInt32(Int32Address, 778899);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadInt32(Int32Address);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(778899, read.Content);
    }

    [Fact]
    public void WriteUInt16_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var write = driver.WriteUInt16(UInt16Address, 12345);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadUInt16(UInt16Address);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)12345, read.Content);
    }

    [Fact]
    public void WriteBool_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var write = driver.WriteBool(BoolAddress, true);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadBool(BoolAddress);
        Assert.True(read.IsSuccess, read.Message);
        Assert.True(read.Content);
    }

    // ──────────── 契约：扩展数据类型（Float/String） ────────────

    [Fact]
    public void WriteFloat_ThenReadBack_ReturnsWrittenValue()
    {
        const float expected = 3.14159f;
        using var driver = CreateLiveDriver();
        driver.Connect();

        var write = driver.WriteFloat(Int32Address, expected);
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadFloat(Int32Address);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(expected, read.Content);
    }

    [Fact]
    public void WriteFloatBatch_ThenReadBack_RoundTrips()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var expected = new[] { 1.5f, -2.25f, 3.75f };
        for (var i = 0; i < expected.Length; i++)
        {
            var addr = string.Format(Int32BatchAddressTemplate, Int32BatchBaseOffset + i * Int32AddressStride);
            var w = driver.WriteFloat(addr, expected[i]);
            Assert.True(w.IsSuccess, $"{addr}: {w.Message}");
        }

        var start = string.Format(Int32BatchAddressTemplate, Int32BatchBaseOffset);
        var result = driver.ReadFloatBatch(start, (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void WriteString_ThenReadBack_ReturnsWrittenValue()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var write = driver.WriteString(StringAddress, "ABC123");
        Assert.True(write.IsSuccess, write.Message);

        var read = driver.ReadString(StringAddress, 20);
        Assert.True(read.IsSuccess, read.Message);
        // 协议差异容忍：Siemens S7 的 String 带 2 字节长度前缀（maxLen/curLen），
        // 其他协议返回纯字符；统一断言"写入内容可读回"（去尾随 \0 后包含）。
        var normalized = read.Content.TrimEnd('\0');
        Assert.Contains("ABC123", normalized, StringComparison.Ordinal);
    }

    // ──────────── 契约：批量读 ────────────

    [Fact]
    public void WriteAndReadInt32Batch_RoundTrips()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        // 驱动无批量写：逐值写连续地址（按 Int32AddressStride 推进）后批量读回
        var expected = new[] { 11, 22, 33 };
        for (var i = 0; i < expected.Length; i++)
        {
            var addr = string.Format(Int32BatchAddressTemplate, Int32BatchBaseOffset + i * Int32AddressStride);
            var w = driver.WriteInt32(addr, expected[i]);
            Assert.True(w.IsSuccess, $"{addr}: {w.Message}");
        }

        var start = string.Format(Int32BatchAddressTemplate, Int32BatchBaseOffset);
        var result = driver.ReadInt32Batch(start, (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public void WriteAndReadBoolBatch_RoundTrips()
    {
        using var driver = CreateLiveDriver();
        driver.Connect();

        var expected = new[] { true, false, true, false };
        for (var i = 0; i < expected.Length; i++)
        {
            var w = driver.WriteBool(FormatBoolBitAddress(i), expected[i]);
            Assert.True(w.IsSuccess, w.Message);
        }

        var result = driver.ReadBoolBatch(BoolBatchStartAddress, (ushort)expected.Length);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(expected, result.Content);
    }

    // ──────────── 契约：批读能力单一事实源 ────────────

    [Fact]
    public void BatchReadCapabilities_MatchDescriptorComputedValues()
    {
        var config = BuildLiveConfig();

        var expected = PlcBrandDescriptors.CreateDefault().Resolve(Brand).GetBatchReadCapabilities(config);

        Assert.Equal(expected, GetDriverBatchCapabilities(config));
    }

    // ──────────── 契约：断线 / 配置切换 / 释放 ────────────

    [Fact]
    public void ReadInt32_Fails_AfterNetworkDisconnect()
    {
        // HslCommunication 客户端在 ConnectClose() 后会自动重连，
        // 因此不能用 driver.Disconnect() 测试断线——必须通过 CutConnection 切断网络。
        using var driver = CreateLiveDriver();
        driver.Connect();
        driver.WriteInt32(Int32Address, 42);

        CutConnection();

        var result = driver.ReadInt32(Int32Address);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Configure_ToNewEndpoint_RecreatesClientAndConnectsToLiveServer()
    {
        var deadPort = AllocateFreePort();
        var deadConfig = BuildLiveConfig();
        deadConfig.Port = deadPort;
        using var driver = CreateDriver(deadConfig);

        Assert.False(driver.Connect().IsSuccess);

        driver.Configure("127.0.0.1", Port);

        Assert.True(driver.Connect().IsSuccess);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var driver = CreateLiveDriver();
        driver.Connect();

        driver.Dispose();
        driver.Dispose();
    }
}
