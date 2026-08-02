using System.Net.Sockets;
using System.IO;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// <see cref="FakePlcDriverBuilder"/> 契约测试：验证 Builder 链式配置与直接配置 FakePlcDriver 等价。
/// 同时充当 Builder 用法的可执行示例。
/// </summary>
[Trait("Category", "Contract")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class FakePlcDriverBuilderTests
{
    [Fact]
    public void Default_Equals_NewFakePlcDriver()
    {
        var direct = new FakePlcDriver();
        var built = FakePlcDriverBuilder.Default().Build();

        Assert.Equal(direct.IsConnected, built.IsConnected);
        Assert.Equal(direct.ShouldFailConnect, built.ShouldFailConnect);
    }

    [Fact]
    public void WithInt32_PresetsAddressValue()
    {
        var plc = new FakePlcDriverBuilder()
            .WithInt32("D100", 42)
            .Build();

        var r = plc.ReadInt32("D100");
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Content);
    }

    [Fact]
    public void WithBool_PresetsAddressValue()
    {
        var plc = new FakePlcDriverBuilder()
            .WithBool("M10", true)
            .Build();

        var r = plc.ReadBool("M10");
        Assert.True(r.IsSuccess);
        Assert.True(r.Content);
    }

    [Fact]
    public void WithBools_BatchPresetsAllAddresses()
    {
        var pairs = new[]
        {
            KeyValuePair.Create("M0", true),
            KeyValuePair.Create("M1", false),
            KeyValuePair.Create("M2", true),
        };

        var plc = new FakePlcDriverBuilder().WithBools(pairs).Build();

        Assert.True(plc.ReadBool("M0").Content);
        Assert.False(plc.ReadBool("M1").Content);
        Assert.True(plc.ReadBool("M2").Content);
    }

    [Fact]
    public void WithFailingAddress_ReadReturnsFail()
    {
        var plc = new FakePlcDriverBuilder()
            .WithInt32("D100", 42)
            .WithFailingAddress("D100")
            .Build();

        var r = plc.ReadInt32("D100");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Disconnected_SetsShouldFailConnect()
    {
        var plc = FakePlcDriverBuilder.Disconnected().Build();

        Assert.True(plc.ShouldFailConnect);
        var r = plc.Connect();
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Connected_False_SetsIsConnectedProperty()
    {
        var plc = new FakePlcDriverBuilder().Connected(false).Build();

        Assert.False(plc.IsConnected);
    }

    [Fact]
    public void WithConnectException_ConnectThrows()
    {
        var ex = new IOException("connect failed");
        var plc = new FakePlcDriverBuilder().WithConnectException(ex).Build();

        var thrown = Assert.Throws<IOException>(() => plc.Connect());
        Assert.Same(ex, thrown);
    }

    [Fact]
    public void WithReadInt32Exception_ReadInt32Throws()
    {
        var ex = new ObjectDisposedException("MelsecMcNet");
        var plc = new FakePlcDriverBuilder().WithReadInt32Exception(ex).Build();

        var thrown = Assert.Throws<ObjectDisposedException>(() => plc.ReadInt32("D100"));
        Assert.Same(ex, thrown);
    }

    [Fact]
    public void WithReadBoolException_ReadBoolThrows()
    {
        var ex = new SocketException(10054);
        var plc = new FakePlcDriverBuilder().WithReadBoolException(ex).Build();

        var thrown = Assert.Throws<SocketException>(() => plc.ReadBool("M10"));
        Assert.Same(ex, thrown);
    }

    [Fact]
    public void WithSocketErrorOnReadBool_Preset_ThrowsSocketException10054()
    {
        var plc = FakePlcDriverBuilder.WithSocketErrorOnReadBool().Build();

        var thrown = Assert.Throws<SocketException>(() => plc.ReadBool("M10"));
        Assert.Equal((SocketError)10054, thrown.SocketErrorCode);
    }

    [Fact]
    public void WithObjectDisposedOnReadInt32_Preset_ThrowsObjectDisposedException()
    {
        var plc = FakePlcDriverBuilder.WithObjectDisposedOnReadInt32().Build();

        Assert.Throws<ObjectDisposedException>(() => plc.ReadInt32("D100"));
    }

    [Fact]
    public void WithIOExceptionOnReadInt32_Preset_ThrowsIOException()
    {
        var plc = FakePlcDriverBuilder.WithIOExceptionOnReadInt32().Build();

        Assert.Throws<IOException>(() => plc.ReadInt32("D100"));
    }

    [Fact]
    public void WithBusinessExceptionOnReadBool_Preset_ThrowsNullReferenceException()
    {
        var plc = FakePlcDriverBuilder.WithBusinessExceptionOnReadBool().Build();

        Assert.Throws<NullReferenceException>(() => plc.ReadBool("M10"));
    }

    /// <summary>验证链式调用累积所有配置：值预设 + 失败地址 + 异常注入 + IsConnected 共存。
    /// 异常注入（全局）优先于值预设与失败地址：ReadInt32Exception 设置后，所有 ReadInt32 都抛异常。</summary>
    [Fact]
    public void ChainedCalls_PreserveAllConfigurations()
    {
        var plc = new FakePlcDriverBuilder()
            .WithInt32("D100", 42)
            .WithBool("M10", true)
            .WithFailingAddress("D200")
            .WithReadInt32Exception(new IOException("boom"))
            .Connected(false)
            .Build();

        // 异常注入优先于值预设与失败地址：所有 ReadInt32 都抛 IOException
        Assert.Throws<IOException>(() => plc.ReadInt32("D100"));
        Assert.Throws<IOException>(() => plc.ReadInt32("D200"));
        // Bool 未配置异常，值预设与失败地址仍生效
        Assert.True(plc.GetBool("M10"));
        // IsConnected 配置仍在
        Assert.False(plc.IsConnected);
    }

    /// <summary>验证隐式转换：Builder 可直接当 FakePlcDriver 使用。</summary>
    [Fact]
    public void ImplicitConversion_ReturnsBuiltDriver()
    {
        FakePlcDriver plc = new FakePlcDriverBuilder().WithInt32("D100", 7);

        Assert.Equal(7, plc.ReadInt32("D100").Content);
    }

    /// <summary>验证 Builder 产出的实例同样能被 PlcConnectionManager 使用（与 new FakePlcDriver 行为一致）。</summary>
    [Fact]
    public void Build_ProducesUsableIPlcDriver()
    {
        var plc = new FakePlcDriverBuilder()
            .WithInt32("D100", 100)
            .Build();
        var settings = new AppSettings();
        var mgr = new PlcConnectionManager(plc, settings);

        mgr.EnsureConnected();

        Assert.True(mgr.IsConnected);
        Assert.Equal(100, plc.ReadInt32("D100").Content);
    }
}
