using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class PlcBrandCodecTests
{
    [Theory]
    [InlineData(PlcBrand.Mitsubishi, "D100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Mitsubishi, "M10", PlcAddressType.MBit)]
    [InlineData(PlcBrand.Siemens, "DB1.DBD100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Siemens, "DB1.DBX0.3", PlcAddressType.MBit)]
    [InlineData(PlcBrand.ModbusTcp, "HR100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.ModbusTcp, "C10", PlcAddressType.MBit)]
    [InlineData(PlcBrand.Omron, "D100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Omron, "CIO10", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Keyence, "DM100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Keyence, "MR10", PlcAddressType.MBit)]
    [InlineData(PlcBrand.Keyence, "D100", PlcAddressType.DWord)]
    [InlineData(PlcBrand.Keyence, "M10", PlcAddressType.MBit)]
    public void Codec_ParsesBrandAddress(PlcBrand brand, string address, PlcAddressType type)
    {
        var settings = new AppSettings();
        settings.PlcConfig.Brand = brand;
        var codec = new PlcAddressCodecResolver(settings).Current;

        var result = codec.Parse(address);

        Assert.True(result.IsValid);
        Assert.Equal(type, result.Type);
    }

    [Fact]
    public void SiemensCodec_AddsDWordByFourBytes()
    {
        var settings = new AppSettings();
        settings.PlcConfig.Brand = PlcBrand.Siemens;
        var codec = new PlcAddressCodecResolver(settings).Current;

        Assert.Equal("DB1.DBD104", codec.Add("DB1.DBD100", 1));
    }

    [Fact]
    public void KeyenceCodec_NormalizesAndAddsNativeAddresses()
    {
        var settings = new AppSettings();
        settings.PlcConfig.Brand = PlcBrand.Keyence;
        var codec = new PlcAddressCodecResolver(settings).Current;

        Assert.Equal("DM100", codec.Normalize(" dm100 "));
        Assert.Equal("DM102", codec.Add("DM100", 1));
        Assert.Equal("MR11", codec.Add("MR10", 1));
        Assert.Equal("W1A2", codec.Add("W1A0", 1));
    }

    [Fact]
    public void ModbusCodec_UsesNumericTransportAddress()
    {
        var settings = new AppSettings();
        settings.PlcConfig.Brand = PlcBrand.ModbusTcp;
        var codec = new PlcAddressCodecResolver(settings).Current;

        Assert.Equal("100", codec.ToTransportAddress("HR100"));
        Assert.Equal("C11", codec.Add("C10", 1));
    }
}
