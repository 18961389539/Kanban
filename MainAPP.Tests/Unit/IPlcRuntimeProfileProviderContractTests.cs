using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// IPlcRuntimeProfileProvider 契约测试：验证运行时配置提供者的行为一致性。
///
/// 契约覆盖：
/// - <see cref="PlcRuntimeProfileProvider.BatchReadCapabilitiesFor"/> 对所有 <see cref="PlcBrand"/> 返回非 null
/// - BatchReadCapabilities 的字段值合理（MaxInt32Length > 0, Int32AddressStride > 0 等）
/// - <see cref="IPlcRuntimeProfileProvider.Current"/> 返回的 Profile 的 Brand 与配置一致
/// - <see cref="IPlcRuntimeProfileProvider.Refresh"/> 后 Version 递增
/// - Refresh(null) 抛 <see cref="ArgumentNullException"/>
/// </summary>
[Trait("Category", "Contract")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public class IPlcRuntimeProfileProviderContractTests
{
    // ──────────── BatchReadCapabilitiesFor 静态方法契约 ────────────

    [Theory]
    [InlineData(PlcBrand.Mitsubishi)]
    [InlineData(PlcBrand.Siemens)]
    [InlineData(PlcBrand.ModbusTcp)]
    [InlineData(PlcBrand.Omron)]
    [InlineData(PlcBrand.Keyence)]
    public void BatchReadCapabilitiesFor_AllBrands_ReturnsValidCapabilities(PlcBrand brand)
    {
        var caps = PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(brand);

        Assert.True(caps.SupportsInt32, $"{brand}: 应支持 Int32 批量读");
        Assert.True(caps.SupportsBool, $"{brand}: 应支持 Bool 批量读");
        Assert.True(caps.MaxInt32Length > 0, $"{brand}: MaxInt32Length 应 > 0");
        Assert.True(caps.MaxBoolLength > 0, $"{brand}: MaxBoolLength 应 > 0");
        Assert.True(caps.Int32AddressStride > 0, $"{brand}: Int32AddressStride 应 > 0");
        Assert.True(caps.BoolAddressStride > 0, $"{brand}: BoolAddressStride 应 > 0");
    }

    [Fact]
    public void BatchReadCapabilitiesFor_Siemens_HasSmallerBatchThanMitsubishi()
    {
        // Siemens S7 协议单次批量读上限远小于三菱 MC 协议
        var siemens = PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(PlcBrand.Siemens);
        var mitsubishi = PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(PlcBrand.Mitsubishi);

        Assert.True(siemens.MaxInt32Length < mitsubishi.MaxInt32Length,
            $"Siemens MaxInt32Length({siemens.MaxInt32Length}) 应小于 Mitsubishi({mitsubishi.MaxInt32Length})");
    }

    [Fact]
    public void BatchReadCapabilitiesFor_Modbus_Int32StrideIs2()
    {
        // Modbus Int32 占 2 个寄存器
        var modbus = PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(PlcBrand.ModbusTcp);
        Assert.Equal(2, modbus.Int32AddressStride);
    }

    // ──────────── IPlcRuntimeProfileProvider 实例契约 ────────────

    private static (PlcRuntimeProfileProvider provider, PlcConfig config) CreateProvider(PlcBrand brand = PlcBrand.Mitsubishi)
    {
        var config = new PlcConfig { Brand = brand };
        var settings = new AppSettings { PlcConfig = config };
        var resolver = new PlcAddressCodecResolver(settings);
        var provider = new PlcRuntimeProfileProvider(settings, resolver);
        return (provider, config);
    }

    [Fact]
    public void Current_BrandMatchesConfiguration()
    {
        var (provider, _) = CreateProvider(PlcBrand.Siemens);
        Assert.Equal(PlcBrand.Siemens, provider.Current.Brand);
    }

    [Fact]
    public void Current_AddressCodecNotNull()
    {
        var (provider, _) = CreateProvider();
        Assert.NotNull(provider.Current.AddressCodec);
    }

    [Fact]
    public void Current_BatchReadCapabilitiesNotNull()
    {
        var (provider, _) = CreateProvider();
        Assert.NotNull(provider.Current.BatchReadCapabilities);
    }

    [Fact]
    public void Current_VersionStartsAt1()
    {
        var (provider, _) = CreateProvider();
        Assert.Equal(1, provider.Current.Version);
    }

    [Fact]
    public void Refresh_IncrementsVersion()
    {
        var (provider, config) = CreateProvider();
        var v0 = provider.Current.Version;

        provider.Refresh(config);
        Assert.Equal(v0 + 1, provider.Current.Version);

        provider.Refresh(config);
        Assert.Equal(v0 + 2, provider.Current.Version);
    }

    [Fact]
    public void Refresh_ChangesBrand()
    {
        var (provider, _) = CreateProvider(PlcBrand.Mitsubishi);
        Assert.Equal(PlcBrand.Mitsubishi, provider.Current.Brand);

        provider.Refresh(new PlcConfig { Brand = PlcBrand.Siemens });
        Assert.Equal(PlcBrand.Siemens, provider.Current.Brand);
    }

    [Fact]
    public void Refresh_Null_ThrowsArgumentNullException()
    {
        var (provider, _) = CreateProvider();
        Assert.Throws<ArgumentNullException>(() => provider.Refresh(null!));
    }

    [Fact]
    public void Refresh_UpdatesBatchReadCapabilities()
    {
        var (provider, _) = CreateProvider(PlcBrand.Mitsubishi);
        var mitsubishiCaps = provider.Current.BatchReadCapabilities;

        provider.Refresh(new PlcConfig { Brand = PlcBrand.Siemens });
        var siemensCaps = provider.Current.BatchReadCapabilities;

        Assert.NotEqual(mitsubishiCaps.MaxInt32Length, siemensCaps.MaxInt32Length);
    }
}
