using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class PlcAddressCapabilityTests
{
    private static IPlcAddressCodec Resolve(PlcBrand brand)
    {
        var settings = new AppSettings { PlcConfig = new PlcConfig { Brand = brand } };
        return new PlcAddressCodecResolver(settings).Resolve(brand);
    }

    [Theory]
    [InlineData(PlcBrand.Mitsubishi, "D100")]
    [InlineData(PlcBrand.Mitsubishi, "M10")]
    [InlineData(PlcBrand.Siemens, "ID100")]
    [InlineData(PlcBrand.Siemens, "DB1.DBD100")]
    [InlineData(PlcBrand.ModbusTcp, "IR100")]
    [InlineData(PlcBrand.ModbusTcp, "DI10")]
    public void ValidAddresses_AreReadable(PlcBrand brand, string address)
    {
        var codec = Resolve(brand);
        var parsed = codec.Parse(address);

        Assert.True(parsed.IsValid, parsed.ErrorMessage);
        Assert.True(codec.CanRead(parsed));
    }

    [Theory]
    [InlineData(PlcBrand.Mitsubishi, "D100")]
    [InlineData(PlcBrand.Mitsubishi, "M10")]
    [InlineData(PlcBrand.Siemens, "DB1.DBD100")]
    [InlineData(PlcBrand.Siemens, "QD100")]
    [InlineData(PlcBrand.ModbusTcp, "HR100")]
    [InlineData(PlcBrand.ModbusTcp, "C10")]
    public void WritableAddresses_AreWritable(PlcBrand brand, string address)
    {
        var codec = Resolve(brand);
        var parsed = codec.Parse(address);

        Assert.True(parsed.IsValid, parsed.ErrorMessage);
        Assert.True(codec.CanWrite(parsed));
    }

    [Theory]
    [InlineData(PlcBrand.Siemens, "ID100")]
    [InlineData(PlcBrand.Siemens, "I10.3")]
    [InlineData(PlcBrand.ModbusTcp, "IR100")]
    [InlineData(PlcBrand.ModbusTcp, "DI10")]
    public void InputAreas_AreReadOnly(PlcBrand brand, string address)
    {
        var codec = Resolve(brand);
        var parsed = codec.Parse(address);

        Assert.True(parsed.IsValid, parsed.ErrorMessage);
        Assert.True(codec.CanRead(parsed));
        Assert.False(codec.CanWrite(parsed));
    }

    [Theory]
    [InlineData(PlcBrand.Siemens, "ID100")]
    [InlineData(PlcBrand.ModbusTcp, "IR100")]
    public void Adapter_RejectsReadOnlyInt32Writes(PlcBrand brand, string address)
    {
        var driver = new FakePlcDriver();
        var settings = new AppSettings { PlcConfig = new PlcConfig { Brand = brand } };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var profileProvider = new PlcRuntimeProfileProvider(settings, codecResolver);
        var adapter = new PlcDeviceAdapter(driver, profileProvider, codecResolver);

        var result = adapter.WriteInt32(address, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlcErrorKind.UnsupportedOperation, result.ErrorKind);
        Assert.Empty(driver.WriteHistory);
    }

    [Theory]
    [InlineData(PlcBrand.Siemens, "I10.3")]
    [InlineData(PlcBrand.ModbusTcp, "DI10")]
    public void Adapter_RejectsReadOnlyBoolWrites(PlcBrand brand, string address)
    {
        var driver = new FakePlcDriver();
        var settings = new AppSettings { PlcConfig = new PlcConfig { Brand = brand } };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var profileProvider = new PlcRuntimeProfileProvider(settings, codecResolver);
        var adapter = new PlcDeviceAdapter(driver, profileProvider, codecResolver);

        var result = adapter.WriteBool(address, true);

        Assert.False(result.IsSuccess);
        Assert.Equal(PlcErrorKind.UnsupportedOperation, result.ErrorKind);
        Assert.Empty(driver.WriteHistory);
    }
}