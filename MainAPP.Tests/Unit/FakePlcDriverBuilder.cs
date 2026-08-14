using System.Net.Sockets;
using System.IO;

namespace MainAPP.Tests.Unit;

/// <summary>
/// <see cref="FakePlcDriver"/> 的流式构建器。
/// </summary>
/// <remarks>
/// 设计目标：
/// <list type="bullet">
///   <item>复杂场景（多地址预设 + 异常注入 + 失败地址）用 Builder 链式调用，单测一目了然。</item>
///   <item>简单场景继续用 <c>new FakePlcDriver()</c>，Builder 不强制使用，避免对 1000+ 既有测试做无意义迁移。</item>
///   <item>提供常用场景的静态工厂（<see cref="Disconnected"/> / <see cref="WithSocketErrorOnReadBool"/>），
///   消除测试中重复的 SocketException(10054) / ObjectDisposedException 等样板代码。</item>
/// </list>
/// 用法示例：
/// <code>
/// // 1. 简单链式
/// var plc = new FakePlcDriverBuilder()
///     .WithInt32("D100", 42)
///     .WithBool("M10", true)
///     .Build();
///
/// // 2. 场景预设：模拟 ScanAlarms 抛 SocketException
/// var plc = FakePlcDriverBuilder
///     .WithSocketErrorOnReadBool()
///     .WithBool(alarm.PlcAddress, false)   // 预设仍需先设置，异常注入优先于值读取
///     .Build();
///
/// // 3. 场景预设：连接失败
/// var plc = FakePlcDriverBuilder.Disconnected();
/// </code>
/// </remarks>
internal sealed class FakePlcDriverBuilder
{
    private readonly FakePlcDriver _driver = new();

    /// <summary>预设 D 字地址的当前值，等价于 <see cref="FakePlcDriver.SetInt32"/>。</summary>
    public FakePlcDriverBuilder WithInt32(string address, int value)
    {
        _driver.SetInt32(address, value);
        return this;
    }

    /// <summary>预设 M 位地址的当前值，等价于 <see cref="FakePlcDriver.SetBool"/>。</summary>
    public FakePlcDriverBuilder WithBool(string address, bool value)
    {
        _driver.SetBool(address, value);
        return this;
    }

    /// <summary>批量预设 M 位（地址 → 值），常用于"多报警初始状态"场景。</summary>
    public FakePlcDriverBuilder WithBools(IEnumerable<KeyValuePair<string, bool>> values)
    {
        foreach (var kv in values)
            _driver.SetBool(kv.Key, kv.Value);
        return this;
    }

    /// <summary>批量预设 D 字（地址 → 值）。</summary>
    public FakePlcDriverBuilder WithInt32s(IEnumerable<KeyValuePair<string, int>> values)
    {
        foreach (var kv in values)
            _driver.SetInt32(kv.Key, kv.Value);
        return this;
    }

    /// <summary>标记指定地址读取时返回 Fail（不抛异常），等价于 <see cref="FakePlcDriver.SetFailing"/>。</summary>
    public FakePlcDriverBuilder WithFailingAddress(string address)
    {
        _driver.SetFailing(address);
        return this;
    }

    /// <summary>设置 <see cref="FakePlcDriver.IsConnected"/> 初始状态（默认 true）。</summary>
    public FakePlcDriverBuilder Connected(bool isConnected)
    {
        _driver.IsConnected = isConnected;
        return this;
    }

    /// <summary>配置 <see cref="FakePlcDriver.ShouldFailConnect"/> = true，Connect() 返回失败。</summary>
    public FakePlcDriverBuilder WithConnectFailure()
    {
        _driver.ShouldFailConnect = true;
        return this;
    }

    /// <summary>配置 <see cref="FakePlcDriver.ConnectException"/>，Connect() 抛指定异常。</summary>
    public FakePlcDriverBuilder WithConnectException(Exception ex)
    {
        _driver.ConnectException = ex;
        return this;
    }

    /// <summary>配置 <see cref="FakePlcDriver.ReadInt32Exception"/>，ReadInt32() 抛指定异常。</summary>
    public FakePlcDriverBuilder WithReadInt32Exception(Exception ex)
    {
        _driver.ReadInt32Exception = ex;
        return this;
    }

    /// <summary>配置 <see cref="FakePlcDriver.ReadBoolException"/>，ReadBool() 抛指定异常。</summary>
    public FakePlcDriverBuilder WithReadBoolException(Exception ex)
    {
        _driver.ReadBoolException = ex;
        return this;
    }

    /// <summary>返回配置好的 <see cref="FakePlcDriver"/> 实例。</summary>
    public FakePlcDriver Build() => _driver;

    /// <summary>隐式转换：让 Builder 在需要 IPlcDriver 的位置直接使用，省略 .Build()。</summary>
    public static implicit operator FakePlcDriver(FakePlcDriverBuilder builder) => builder.Build();

    // ──────────── 场景预设（消除重复样板） ────────────

    /// <summary>默认构建器：已连接、无预设值。等价于 <c>new FakePlcDriver()</c>，仅作语义占位。</summary>
    public static FakePlcDriverBuilder Default() => new FakePlcDriverBuilder();

    /// <summary>场景：连接失败（<see cref="FakePlcDriver.ShouldFailConnect"/>=true）。</summary>
    public static FakePlcDriverBuilder Disconnected() => new FakePlcDriverBuilder().WithConnectFailure();

    /// <summary>场景：ScanAlarms 阶段抛 SocketException(10054) ConnectionReset，模拟 PLC 断线。
    /// 配合 <see cref="WithBool"/> 预设报警初值后使用。</summary>
    public static FakePlcDriverBuilder WithSocketErrorOnReadBool()
        => new FakePlcDriverBuilder().WithReadBoolException(new SocketException(10054));

    /// <summary>场景：ScanDefects / ScanCounterAlarms 阶段抛 ObjectDisposedException，
    /// 模拟 HslCommunication 客户端被释放后读取。</summary>
    public static FakePlcDriverBuilder WithObjectDisposedOnReadInt32()
        => new FakePlcDriverBuilder().WithReadInt32Exception(new ObjectDisposedException("MelsecMcNet"));

    /// <summary>场景：读取阶段抛 IOException，模拟通信链路中断。</summary>
    public static FakePlcDriverBuilder WithIOExceptionOnReadInt32()
        => new FakePlcDriverBuilder().WithReadInt32Exception(new IOException("PLC 通信中断"));

    /// <summary>场景：ScanAlarms 抛 NullReferenceException，模拟业务异常（不应触发 MarkDisconnected）。</summary>
    public static FakePlcDriverBuilder WithBusinessExceptionOnReadBool()
        => new FakePlcDriverBuilder().WithReadBoolException(new NullReferenceException("模拟业务异常：内部状态被意外置空"));
}
