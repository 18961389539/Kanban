using Kanban.Collector.Core.Models;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// PLC 驱动路由器：品牌变更时替换该连接槽位中的驱动。
///
/// 并发设计（锁外 IO + 引用安全）：
/// - <see cref="_sync"/> 只保护「当前驱动引用」的读取/替换与引用计数，不再包裹阻塞式 PLC IO；
///   IO 的串行化由底层驱动实例锁保证（单连接、Hsl 客户端非线程安全的物理约束）。
/// - 每个 IO 操作在锁内对当前驱动 <see cref="DriverRef"/> 增加引用计数，锁外执行操作，
///   finally 归还引用；品牌切换时旧驱动标记退役，若仍有在途 IO 则等到最后一个引用
///   归还后才真正 Dispose，避免「旧驱动上 IO 在途期间被释放」的竞态。
/// - 因此锁的持有时间与 IO 阻塞时长解耦：PLC 慢响应/超时不再阻塞其他线程的取引用，
///   切换驱动也无需等待在途 IO 完成。
/// </summary>
public sealed class SharedPlcDriverRouter : IPlcDriver
{
    /// <summary>当前活动驱动 + 引用计数。仅可经 <see cref="_sync"/> 访问。</summary>
    private sealed class DriverRef
    {
        public DriverRef(IPlcDriver driver)
        {
            Driver = driver;
        }

        public IPlcDriver Driver { get; }

        /// <summary>在途引用数（IO/连接/配置操作借用期间为正值）。锁内访问。</summary>
        public int Uses;

        /// <summary>已被替换退役，引用归零后可释放。锁内访问。</summary>
        public bool Retired;
    }

    private readonly object _sync = new();
    private readonly ISharedPlcDriverFactory _factory;
    private readonly IPlcRuntimeProfileProvider _profileProvider;
    private DriverRef _current;
    private PlcConfig _currentConfig;
    private PlcBrand _currentBrand;
    private bool _disposed;

    public SharedPlcDriverRouter(
        ISharedPlcDriverFactory factory,
        AppSettings settings,
        IPlcRuntimeProfileProvider profileProvider)
        : this(factory, settings.PlcConfig, profileProvider)
    {
    }

    public SharedPlcDriverRouter(
        ISharedPlcDriverFactory factory,
        PlcConfig config,
        IPlcRuntimeProfileProvider profileProvider)
    {
        ArgumentNullException.ThrowIfNull(config);
        _factory = factory;
        _profileProvider = profileProvider;
        _current = new DriverRef(factory.Create(config));
        _currentConfig = CloneConfig(config);
        _currentBrand = config.Brand;
    }

    public void Configure(string endpoint, int port)
    {
        PlcConfig baseConfig;
        lock (_sync)
        {
            // 锁内取当前配置快照，避免并发读写 _currentConfig 的引用竞态。
            baseConfig = CloneConfig(_currentConfig);
        }
        baseConfig.IpAddress = endpoint;
        baseConfig.Port = port;
        Configure(baseConfig);
    }

    public void Configure(PlcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 品牌变更路径在锁内完成（需原子替换 _current 引用），同品牌路径锁内仅借用引用、
        // 锁外执行底层 Configure（可能阻塞，但不占用 Router 锁）。两种路径都不再持有
        // <see cref="_sync"/> 执行阻塞式底层调用（品牌路径中 _factory.Create 仅创建对象）。
        bool brandSwitched = false;
        IPlcDriver? disposeOutsideLock = null;
        DriverRef? reference = null;

        lock (_sync)
        {
            if (_disposed) return;
            if (config.Brand != _currentBrand)
            {
                brandSwitched = true;
                var replacement = new DriverRef(_factory.Create(config));
                _current.Retired = true;
                if (_current.Uses == 0)
                {
                    // 无在途操作，为不拖长临界区，移到锁外统一释放。
                    disposeOutsideLock = _current.Driver;
                }
                _current = replacement;
                _currentBrand = config.Brand;
                _profileProvider.Refresh(config);
                _currentConfig = CloneConfig(config);
            }
            else
            {
                // 同品牌仅改参数：借用当前驱动引用，锁外执行配置变更。
                reference = AcquireLocked();
            }
        }

        if (brandSwitched)
        {
            disposeOutsideLock?.Dispose();
            return;
        }

        // 同品牌路径：锁外转发给底层驱动（底层自身有配置 key 早退），
        // 期间其他线程的取引用/IO 不被阻塞。
        try
        {
            reference!.Driver.Configure(config);
        }
        finally
        {
            Release(reference!);
        }

        lock (_sync)
        {
            if (_disposed) return;
            // 仅当当前活动驱动仍是本次配置借用的驱动时才回写共享配置状态；
            // 若锁外执行期间发生了品牌切换/Dispose，当前驱动已更换，本次配置已过时，直接丢弃。
            if (ReferenceEquals(_current.Driver, reference.Driver))
            {
                _profileProvider.Refresh(config);
                _currentConfig = CloneConfig(config);
            }
        }
    }

    public PlcOperationResult Connect() => WithCurrent(driver => driver.Connect());
    public PlcOperationResult Disconnect() => WithCurrent(driver => driver.Disconnect());
    public PlcOperationResult<ushort> ReadUInt16(string address) => WithCurrent(driver => driver.ReadUInt16(address));
    public PlcOperationResult<int> ReadInt32(string address) => WithCurrent(driver => driver.ReadInt32(address));
    public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length) => WithCurrent(driver => driver.ReadInt32Batch(address, length));
    public PlcOperationResult<bool> ReadBool(string address) => WithCurrent(driver => driver.ReadBool(address));
    public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => WithCurrent(driver => driver.ReadBoolBatch(address, length));
    public PlcOperationResult<float> ReadFloat(string address) => WithCurrent(driver => driver.ReadFloat(address));
    public PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length) => WithCurrent(driver => driver.ReadFloatBatch(address, length));
    public PlcOperationResult<string> ReadString(string address, ushort length) => WithCurrent(driver => driver.ReadString(address, length));
    public PlcOperationResult WriteUInt16(string address, ushort value) => WithCurrent(driver => driver.WriteUInt16(address, value));
    public PlcOperationResult WriteInt32(string address, int value) => WithCurrent(driver => driver.WriteInt32(address, value));
    public PlcOperationResult WriteBool(string address, bool value) => WithCurrent(driver => driver.WriteBool(address, value));
    public PlcOperationResult WriteFloat(string address, float value) => WithCurrent(driver => driver.WriteFloat(address, value));
    public PlcOperationResult WriteString(string address, string value) => WithCurrent(driver => driver.WriteString(address, value));

    /// <summary>
    /// 锁内借用当前驱动的引用（强制引用计数生命周期）：借用期间调用方持引用可安全使用驱动，
    /// 即使品牌切换也不会在途释放。必须 Dispose。
    /// </summary>
    private DriverRef AcquireLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedPlcDriverRouter));
        _current.Uses++;
        return _current;
    }

    private TResult WithCurrent<TResult>(Func<IPlcDriver, TResult> operation)
    {
        // 锁内仅做引用交换：获取当前驱动并借用。IO 在锁外执行，缩短锁持有时间。
        DriverRef reference;
        lock (_sync)
        {
            reference = AcquireLocked();
        }
        try
        {
            return operation(reference.Driver);
        }
        finally
        {
            Release(reference);
        }
    }

    /// <summary>归还驱动引用；若驱动已退役且引用归零，则在锁外释放。</summary>
    private void Release(DriverRef reference)
    {
        IPlcDriver? disposeOutsideLock = null;
        lock (_sync)
        {
            if (reference.Uses > 0) reference.Uses--;
            if (reference.Retired && reference.Uses == 0)
                disposeOutsideLock = reference.Driver;
        }
        disposeOutsideLock?.Dispose();
    }

    public void Dispose()
    {
        IPlcDriver? disposeOutsideLock = null;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current.Retired = true;
            if (_current.Uses == 0)
                disposeOutsideLock = _current.Driver;
            // 若仍有在途 IO，由最后一个 Release 负责释放。
        }
        disposeOutsideLock?.Dispose();
    }

    private static PlcConfig CloneConfig(PlcConfig source) => source.CreateSnapshot();
}