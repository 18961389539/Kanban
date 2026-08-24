using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

public sealed record PlcRuntimeProfile(
    PlcBrand Brand,
    IPlcAddressCodec AddressCodec,
    BatchReadCapabilities BatchReadCapabilities,
    long Version,
    PlcConfig Config);

public interface IPlcRuntimeProfileProvider
{
    PlcRuntimeProfile Current { get; }
    void Refresh(PlcConfig config);
}

public sealed class PlcRuntimeProfileProvider : IPlcRuntimeProfileProvider
{
    private readonly object _sync = new();
    private readonly IPlcBrandRegistry _brandRegistry;
    private PlcRuntimeProfile _current;
    private long _version;

    public PlcRuntimeProfileProvider(
        AppSettings settings,
        IPlcAddressCodecResolver codecResolver,
        IPlcBrandRegistry? brandRegistry = null)
        : this(settings.PlcConfig, codecResolver, brandRegistry)
    {
    }

    public PlcRuntimeProfileProvider(
        PlcConfig config,
        IPlcAddressCodecResolver codecResolver,
        IPlcBrandRegistry? brandRegistry = null)
    {
        _ = codecResolver;
        _brandRegistry = brandRegistry ?? PlcBrandDescriptors.CreateDefault();
        _current = Build(config, ++_version);
    }

    public PlcRuntimeProfile Current
    {
        get { lock (_sync) return _current; }
    }

    public void Refresh(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_sync)
        {
            _current = Build(config, ++_version);
        }
    }

    private PlcRuntimeProfile Build(PlcConfig config, long version)
    {
        var snapshot = config.CreateSnapshot();
        var descriptor = _brandRegistry.Resolve(snapshot.Brand);
        return new(
            snapshot.Brand,
            descriptor.CreateAddressCodec(snapshot),
            descriptor.GetBatchReadCapabilities(snapshot),
            version,
            snapshot);
    }

    public static BatchReadCapabilities BatchReadCapabilitiesFor(
        PlcBrand brand,
        int siemensBatchInt32Limit = 55,
        int omronReadSplits = 500,
        int modbusBatchInt32Limit = 62)
    {
        var config = new PlcConfig { Brand = brand };
        config.Siemens.BatchInt32Limit = siemensBatchInt32Limit;
        config.Omron.ReadSplits = omronReadSplits;
        config.ModbusTcp.BatchInt32Limit = modbusBatchInt32Limit;
        return PlcBrandDescriptors.CreateDefault().Resolve(brand).GetBatchReadCapabilities(config);
    }
}
