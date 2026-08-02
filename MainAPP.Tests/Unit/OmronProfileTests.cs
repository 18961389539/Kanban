using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class OmronProfileTests
{
    [Fact]
    public void OmronProfile_UsesFinsCapabilities()
    {
        var settings = new AppSettings
        {
            PlcConfig = new PlcConfig { Brand = PlcBrand.Omron, OmronReadSplits = 500 }
        };
        var resolver = new PlcAddressCodecResolver(settings);
        var provider = new PlcRuntimeProfileProvider(settings, resolver);

        Assert.Equal(PlcBrand.Omron, provider.Current.Brand);
        Assert.Equal(250, provider.Current.BatchReadCapabilities.MaxInt32Length);
        Assert.Equal(2, provider.Current.BatchReadCapabilities.Int32AddressStride);
    }
}
