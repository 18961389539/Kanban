using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class SharedPlcDriverFactoryTests
{
    [Theory]
    [InlineData(PlcBrand.Mitsubishi, "HslPlcDriver")]
    [InlineData(PlcBrand.Siemens, "HslSiemensPlcDriver")]
    [InlineData(PlcBrand.ModbusTcp, "HslModbusTcpDriver")]
    [InlineData(PlcBrand.Omron, "HslOmronFinsDriver")]
    [InlineData(PlcBrand.Keyence, "HslKeyenceMcDriver")]
    public void Create_SelectsSharedDriverByBrand(PlcBrand brand, string expectedTypeName)
    {
        var factory = new HslSharedPlcDriverFactory(NullLoggerFactory.Instance);
        using var driver = factory.Create(new PlcConfig { Brand = brand });

        Assert.Equal(expectedTypeName, driver.GetType().Name);
    }
}
