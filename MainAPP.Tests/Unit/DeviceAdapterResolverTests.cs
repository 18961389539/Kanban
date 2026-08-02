using MainAPP.Models;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class DeviceAdapterResolverTests
{
    [Fact]
    public void Resolve_UsesExplicitProtocolKey()
    {
        var adapter = new PlcDeviceAdapter(new FakePlcDriver());
        var resolver = new DeviceAdapterResolver([adapter]);

        var resolved = resolver.Resolve(new Device { Name = "测试设备" });

        Assert.Same(adapter, resolved);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProtocolAdapters()
    {
        var first = new PlcDeviceAdapter(new FakePlcDriver());
        var second = new PlcDeviceAdapter(new FakePlcDriver());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new DeviceAdapterResolver([first, second]));

        Assert.Contains("PLC 品牌", exception.Message);
    }
}
