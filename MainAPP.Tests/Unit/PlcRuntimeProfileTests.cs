using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class PlcRuntimeProfileTests
{
    [Fact]
    public void Refresh_ReplacesBrandCodecAndIncrementsVersion()
    {
        var settings = new AppSettings();
        var resolver = new PlcAddressCodecResolver(settings);
        var provider = new PlcRuntimeProfileProvider(settings, resolver);
        var initial = provider.Current;
        settings.PlcConfig.Brand = PlcBrand.ModbusTcp;
        settings.PlcConfig.ModbusRegisterFunction = 4;
        var config = new PlcConfig
        {
            Brand = PlcBrand.ModbusTcp,
            IpAddress = settings.PlcConfig.IpAddress,
            Port = PlcConfig.GetDefaultPort(PlcBrand.ModbusTcp),
            ModbusRegisterFunction = 4,
        };
        provider.Refresh(config);

        Assert.True(provider.Current.Version > initial.Version);
        Assert.Equal(PlcBrand.ModbusTcp, provider.Current.Brand);
        Assert.Equal("x=4;100", provider.Current.AddressCodec.ToTransportAddress("HR100"));
    }
}
