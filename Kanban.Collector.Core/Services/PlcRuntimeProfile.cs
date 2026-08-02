using MainAPP.Models;

namespace MainAPP.Services;

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
    private readonly IPlcAddressCodecResolver _codecResolver;
    private PlcRuntimeProfile _current;
    private long _version;

    public PlcRuntimeProfileProvider(
        AppSettings settings,
        IPlcAddressCodecResolver codecResolver)
    {
        _codecResolver = codecResolver;
        _current = Build(settings.PlcConfig, ++_version);
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
        return new(
        snapshot.Brand,
        config.Brand == PlcBrand.ModbusTcp
            ? new ModbusTcpAddressCodec(snapshot)
            : _codecResolver.Resolve(snapshot.Brand),
        BatchReadCapabilitiesFor(snapshot.Brand, snapshot.SiemensBatchInt32Limit, snapshot.OmronReadSplits),
        version,
        snapshot);
    }

    public static BatchReadCapabilities BatchReadCapabilitiesFor(PlcBrand brand, int siemensBatchInt32Limit = 55, int omronReadSplits = 500) => brand switch
    {
        PlcBrand.Siemens => new(true, (ushort)Math.Clamp(siemensBatchInt32Limit, 1, 55), 4, true, 2000, 1),
        PlcBrand.ModbusTcp => new(true, 62, 2, true, 2000, 1),
        PlcBrand.Omron => new(true, (ushort)Math.Clamp(omronReadSplits / 2, 1, 499), 2, true, 2000, 1),
        _ => new(true, 480, 2, true, 2000, 1),
    };
}
