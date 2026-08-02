using Kanban.Core.Models;

namespace Kanban.Core.Services;

/// <summary>
/// 共享 PLC 驱动路由器：应用始终只有一个活动 PLC 连接；品牌变更时替换该连接槽位中的驱动。
/// </summary>
public sealed class SharedPlcDriverRouter : IPlcDriver
{
    private readonly object _sync = new();
    private readonly ISharedPlcDriverFactory _factory;
    private readonly IPlcRuntimeProfileProvider _profileProvider;
    private IPlcDriver _current;
    private PlcConfig _currentConfig;
    private PlcBrand _currentBrand;
    private bool _disposed;

    public SharedPlcDriverRouter(
        ISharedPlcDriverFactory factory,
        AppSettings settings,
        IPlcRuntimeProfileProvider profileProvider)
    {
        _factory = factory;
        _profileProvider = profileProvider;
        _current = factory.Create(settings.PlcConfig);
        _currentConfig = CloneConfig(settings.PlcConfig);
        _currentBrand = settings.PlcConfig.Brand;
    }

    public void Configure(string endpoint, int port)
    {
        lock (_sync)
        {
            var config = CloneConfig(_currentConfig);
            config.IpAddress = endpoint;
            config.Port = port;
            _current.Configure(config);
            _currentConfig = config;
            _profileProvider.Refresh(config);
        }
    }

    public void Configure(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_sync)
        {
            if (_disposed) return;
            if (config.Brand != _currentBrand)
            {
                var replacement = _factory.Create(config);
                try { _current.Dispose(); } catch { }
                _current = replacement;
                _currentBrand = config.Brand;
                _profileProvider.Refresh(config);
            }
            else
            {
                _current.Configure(config);
                _profileProvider.Refresh(config);
            }
            _currentConfig = CloneConfig(config);
        }
    }

    public PlcOperationResult Connect() => WithCurrent(driver => driver.Connect());
    public PlcOperationResult Disconnect() => WithCurrent(driver => driver.Disconnect());
    public PlcOperationResult<ushort> ReadUInt16(string address) => WithCurrent(driver => driver.ReadUInt16(address));
    public PlcOperationResult<int> ReadInt32(string address) => WithCurrent(driver => driver.ReadInt32(address));
    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length) => WithCurrent(driver => driver.ReadInt32Batch(address, length));
    public PlcOperationResult<bool> ReadBool(string address) => WithCurrent(driver => driver.ReadBool(address));
    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => WithCurrent(driver => driver.ReadBoolBatch(address, length));
    public PlcOperationResult WriteUInt16(string address, ushort value) => WithCurrent(driver => driver.WriteUInt16(address, value));
    public PlcOperationResult WriteInt32(string address, int value) => WithCurrent(driver => driver.WriteInt32(address, value));
    public PlcOperationResult WriteBool(string address, bool value) => WithCurrent(driver => driver.WriteBool(address, value));

    private TResult WithCurrent<TResult>(Func<IPlcDriver, TResult> operation)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SharedPlcDriverRouter));
            return operation(_current);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current.Dispose();
        }
    }

    private static PlcConfig CloneConfig(PlcConfig source) => source.CreateSnapshot();
}
