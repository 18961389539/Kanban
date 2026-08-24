using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Xunit;

namespace MainAPP.Tests.Unit;


[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public sealed class DeviceAdapterResolverTests
{
    private sealed class StaticProfileProvider(PlcRuntimeProfile profile) : IPlcRuntimeProfileProvider
    {
        public PlcRuntimeProfile Current { get; } = profile;
        public void Refresh(PlcConfig config) { }
    }

    private static PlcRuntimeProfile CreateProfile(string protocolKey, PlcBrand brand)
    {
        var config = new PlcConfig { ProtocolKey = protocolKey, Brand = brand };
        return new PlcRuntimeProfile(
            brand,
            new MitsubishiAddressCodec(),
            PlcRuntimeProfileProvider.BatchReadCapabilitiesFor(brand),
            1,
            config);
    }

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

    [Fact]
    public void Resolve_UsesDeviceConnectionProfile()
    {
        var first = new PlcDeviceAdapter(
            new FakePlcDriver(),
            new StaticProfileProvider(CreateProfile("alpha", PlcBrand.Mitsubishi)));
        var second = new PlcDeviceAdapter(
            new FakePlcDriver(),
            new StaticProfileProvider(CreateProfile("beta", PlcBrand.Siemens)));
        var settings = new AppSettings();
        settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "alpha-device",
            Config = new PlcConfig { ProtocolKey = "alpha", Brand = PlcBrand.Mitsubishi },
        });
        settings.ConnectionProfiles.Add(new ConnectionProfile
        {
            Id = "beta-device",
            Config = new PlcConfig { ProtocolKey = "beta", Brand = PlcBrand.Siemens },
        });
        var resolver = new DeviceAdapterResolver([first, second], settings);

        Assert.Same(first, resolver.Resolve(new Device { ConnectionProfileId = "alpha-device" }));
        Assert.Same(second, resolver.Resolve(new Device { ConnectionProfileId = "beta-device" }));
    }

    [Fact]
    public void Resolve_UnknownConnectionProfileThrows()
    {
        var adapter = new PlcDeviceAdapter(new FakePlcDriver());
        var resolver = new DeviceAdapterResolver([adapter], new AppSettings());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(new Device { Name = "Missing profile", ConnectionProfileId = "missing" }));

        Assert.Contains("不存在的连接档案", exception.Message);
    }
}
