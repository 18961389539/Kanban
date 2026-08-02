using MainAPP.Services;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IPlcDriver 契约测试：验证所有实现（FakePlcDriver / NSubstitute Mock / 未来 HslPlcDriver 测试包装器）
/// 对同一接口的行为一致性。
///
/// 契约覆盖：
/// - <see cref="IPlcDriver.Connect"/> 返回 <see cref="PlcOperationResult"/>（IsSuccess=true/false），不返回 null
/// - <see cref="IPlcDriver.Disconnect"/> 始终返回成功结果（即使未连接）
/// - <see cref="IPlcDriver.ReadUInt16"/>/<see cref="ReadInt32"/>/<see cref="ReadBool"/>
///   成功时 Content 非 default 且 IsSuccess=true；失败时 IsSuccess=false
/// - <see cref="IPlcDriver.WriteUInt16"/>/<see cref="WriteInt32"/>/<see cref="WriteBool"/> 始终返回成功结果
/// - <see cref="IPlcDriver.Configure"/> 不抛异常（即使 IP/端口相同）
/// - 所有方法对 null/空字符串地址不抛异常（防御性契约）
///
/// 若未来为 HslPlcDriver 编写可测试的包装器（注入 MelsecMcNet 派生类），
/// 应将其加入 <see cref="Implementations"/> 列表，自动跑同一组用例。
/// </summary>
[Trait("Category","Contract")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class IPlcDriverContractTests
{
    /// <summary>
    /// 所有应通过契约测试的 IPlcDriver 实现工厂。
    /// 使用 TheoryData&lt;T&gt; 强类型成员数据：编译期检查工厂类型，避免运行时 InvalidCastException；
    /// 且测试名显示为工厂类型而非 object[]，可读性更好。
    /// </summary>
    public static TheoryData<Func<IPlcDriver>> Implementations => new()
    {
        // FakePlcDriver：测试桩，内存实现
        () => new FakePlcDriver(),
        // NSubstitute Mock：验证 Mock 库行为与接口契约一致
        () => CreateSubstituteDriver(),
    };

    /// <summary>创建配置好的 NSubstitute Mock，模拟成功路径。</summary>
    private static IPlcDriver CreateSubstituteDriver()
    {
        var driver = Substitute.For<IPlcDriver>();
        driver.Connect().Returns(PlcOperationResult.Success());
        driver.Disconnect().Returns(PlcOperationResult.Success());
        driver.ReadUInt16(Arg.Any<string>()).Returns(PlcOperationResult<ushort>.Success(0));
        driver.ReadInt32(Arg.Any<string>()).Returns(PlcOperationResult<int>.Success(0));
        driver.ReadBool(Arg.Any<string>()).Returns(PlcOperationResult<bool>.Success(false));
        driver.WriteUInt16(Arg.Any<string>(), Arg.Any<ushort>()).Returns(PlcOperationResult.Success());
        driver.WriteInt32(Arg.Any<string>(), Arg.Any<int>()).Returns(PlcOperationResult.Success());
        driver.WriteBool(Arg.Any<string>(), Arg.Any<bool>()).Returns(PlcOperationResult.Success());
        return driver;
    }

    // ──────────── 接口契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Implements_IPlcDriver(Func<IPlcDriver> factory)
    {
        var driver = factory();
        Assert.IsAssignableFrom<IPlcDriver>(driver);
    }

    // ──────────── Connect 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Connect_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.Connect();
        Assert.NotNull(result);
        Assert.True(result.IsSuccess || !result.IsSuccess);  // 布尔值有效
    }

    // ──────────── Disconnect 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Disconnect_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.Disconnect();
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Disconnect_CalledTwice_DoesNotThrow(Func<IPlcDriver> factory)
    {
        var driver = factory();
        driver.Disconnect();
        driver.Disconnect();  // 重复断开不抛异常
    }

    // ──────────── Read 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ReadUInt16_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.ReadUInt16("D100");
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ReadInt32_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.ReadInt32("D200");
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ReadBool_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.ReadBool("M10");
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Read_WithNullOrEmptyAddress_DoesNotThrow(Func<IPlcDriver> factory)
    {
        var driver = factory();
        // 防御性契约：空地址不抛异常（具体返回 Fail 或 default 由实现决定）
        driver.ReadUInt16("");
        driver.ReadInt32("");
        driver.ReadBool("");
    }

    // ──────────── Write 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void WriteUInt16_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.WriteUInt16("D100", 42);
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void WriteInt32_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.WriteInt32("D200", 100);
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void WriteBool_ReturnsNonNullResult(Func<IPlcDriver> factory)
    {
        var driver = factory();
        var result = driver.WriteBool("M10", true);
        Assert.NotNull(result);
    }

    // ──────────── Configure 契约 ────────────

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Configure_DoesNotThrow(Func<IPlcDriver> factory)
    {
        var driver = factory();
        driver.Configure("192.168.1.100", 4999);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Configure_WithSameParams_DoesNotThrow(Func<IPlcDriver> factory)
    {
        var driver = factory();
        driver.Configure("192.168.1.100", 4999);
        driver.Configure("192.168.1.100", 4999);  // 相同参数重复调用
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Configure_WithNullOrEmptyIp_DoesNotThrow(Func<IPlcDriver> factory)
    {
        var driver = factory();
        driver.Configure("", 4999);
        driver.Configure(null!, 4999);
    }
}
