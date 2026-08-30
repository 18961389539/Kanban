using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Xunit;

namespace MainAPP.Tests.Unit;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
[Trait("Requires", "None")]
public sealed class SharedPlcDriverRouterTests
{
    /// <summary>
    /// 可控阻塞的测试驱动：可让 ReadInt32 挂起（模拟慢 IO），并跟踪 Dispose 时机，
    /// 用于验证 Router「锁外 IO + 引用安全」语义。
    /// </summary>
    private sealed class BlockablePlcDriver : IPlcDriver
    {
        public BlockablePlcDriver(string tag) => Tag = tag;

        public string Tag { get; }

        /// <summary>设置后 ReadInt32 会阻塞直到放行。</summary>
        public ManualResetEventSlim? BlockGate { get; set; }

        /// <summary>ReadInt32 已进入（已取得 Router 引用）的信号。</summary>
        public ManualResetEventSlim Entered { get; } = new();

        /// <summary>ReadInt32 进入次数（含并发，Interlocked 计数）。</summary>
        public int EnterCount;

        public int DisposeCount { get; private set; }

        /// <summary>Configure 调用次数（含阻塞等待期间进入的次数）。</summary>
        public int ConfigureEnterCount;

        /// <summary>设置后 Configure(PlcConfig) 会阻塞直到放行（模拟慢配置变更）。</summary>
        public ManualResetEventSlim? ConfigureGate { get; set; }

        public int LastConfiguredPort { get; private set; }

        public void Dispose() => DisposeCount++;

        public void Configure(PlcConfig config)
        {
            Interlocked.Increment(ref ConfigureEnterCount);
            ConfigureGate?.Wait();
            LastConfiguredPort = config.Port;
        }

        public PlcOperationResult<int> ReadInt32(string address)
        {
            Interlocked.Increment(ref EnterCount);
            Entered.Set();
            BlockGate?.Wait();
            return PlcOperationResult<int>.Success(42);
        }

        public void Configure(string endpoint, int port) => LastConfiguredPort = port;
        public PlcOperationResult Connect() => PlcOperationResult.Success();
        public PlcOperationResult Disconnect() => PlcOperationResult.Success();
        public PlcOperationResult<ushort> ReadUInt16(string address) => PlcOperationResult<ushort>.Fail("unsupported");
        public PlcOperationResult<int[]> ReadInt32Batch(string address, ushort length) => PlcOperationResult<int[]>.Fail("unsupported");
        public PlcOperationResult<bool> ReadBool(string address) => PlcOperationResult<bool>.Fail("unsupported");
        public PlcOperationResult<bool[]> ReadBoolBatch(string address, ushort length) => PlcOperationResult<bool[]>.Fail("unsupported");
        public PlcOperationResult<float> ReadFloat(string address) => PlcOperationResult<float>.Fail("unsupported");
        public PlcOperationResult<float[]> ReadFloatBatch(string address, ushort length) => PlcOperationResult<float[]>.Fail("unsupported");
        public PlcOperationResult<string> ReadString(string address, ushort length) => PlcOperationResult<string>.Fail("unsupported");
        public PlcOperationResult WriteUInt16(string address, ushort value) => PlcOperationResult.Fail("unsupported");
        public PlcOperationResult WriteInt32(string address, int value) => PlcOperationResult.Fail("unsupported");
        public PlcOperationResult WriteBool(string address, bool value) => PlcOperationResult.Fail("unsupported");
        public PlcOperationResult WriteFloat(string address, float value) => PlcOperationResult.Fail("unsupported");
        public PlcOperationResult WriteString(string address, string value) => PlcOperationResult.Fail("unsupported");
    }

    private sealed class SwitchingDriverFactory : ISharedPlcDriverFactory
    {
        public List<BlockablePlcDriver> Created { get; } = [];

        public IPlcDriver Create(PlcConfig config)
        {
            var driver = new BlockablePlcDriver($"#{Created.Count + 1}");
            Created.Add(driver);
            return driver;
        }
    }

    private static PlcConfig Config(PlcBrand brand) =>
        new() { Brand = brand, IpAddress = "10.0.0.1", Port = 4999, TimeoutMs = 5000 };

    private static SharedPlcDriverRouter BuildRouting(
        SwitchingDriverFactory factory,
        PlcConfig initial)
    {
        var settings = new AppSettings { PlcConfig = initial };
        var codecResolver = new PlcAddressCodecResolver(settings);
        var profileProvider = new PlcRuntimeProfileProvider(settings, codecResolver);
        return new SharedPlcDriverRouter(factory, settings.PlcConfig, profileProvider);
    }

    /// <summary>品牌切换不阻塞已开始的 IO（锁外执行），且旧驱动在引用归零后延迟释放。</summary>
    [Fact]
    public async Task BrandSwitch_WithInFlightIo_ReleasesOldDriverOnlyAfterIoCompletes()
    {
        var factory = new SwitchingDriverFactory();
        using var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var oldDriver = factory.Created.Single();

        // 阻塞住正在执行的 IO，模拟慢 PLC 读
        var gate = new ManualResetEventSlim(false);
        oldDriver.BlockGate = gate;
        var ioTask = Task.Run(() => router.ReadInt32("D0"));

        // 等 IO 真正进入驱动（已取得 Router 引用）
        Assert.True(oldDriver.Entered.Wait(TimeSpan.FromSeconds(5)), "IO 未进入驱动");

        // 在 IO 在途期间切换品牌：旧驱动不应被立即释放（Uses=1）
        router.Configure(Config(PlcBrand.Siemens));
        Assert.Equal(0, oldDriver.DisposeCount);

        // 放行 IO：完成后引用归零，旧驱动才被真正释放
        gate.Set();
        var result = await ioTask;
        Assert.True(result.IsSuccess);

        Assert.Equal(1, oldDriver.DisposeCount);
    }

    /// <summary>无在途 IO 时品牌切换立即释放旧驱动，且新驱动接管后续操作。</summary>
    [Fact]
    public void BrandSwitch_WithoutInFlightIo_ReleasesOldDriverImmediately()
    {
        var factory = new SwitchingDriverFactory();
        using var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var oldDriver = factory.Created.Single();

        router.Configure(Config(PlcBrand.ModbusTcp));

        Assert.Equal(1, oldDriver.DisposeCount);
        Assert.Equal(2, factory.Created.Count);

        var newDriver = factory.Created[1];
        Assert.Equal(0, newDriver.DisposeCount);
    }

    /// <summary>IO 在途时 Dispose 路由器：驱动延迟到引用归零才释放。</summary>
    [Fact]
    public async Task Dispose_WithInFlightIo_ReleasesDriverAfterIoCompletes()
    {
        var factory = new SwitchingDriverFactory();
        var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var driver = factory.Created.Single();

        var gate = new ManualResetEventSlim(false);
        driver.BlockGate = gate;
        var ioTask = Task.Run(() => router.ReadInt32("D0"));
        Assert.True(driver.Entered.Wait(TimeSpan.FromSeconds(5)), "IO 未进入驱动");

        router.Dispose();
        Assert.Equal(0, driver.DisposeCount); // IO 在途，延迟释放

        gate.Set();
        _ = await ioTask;

        Assert.Equal(1, driver.DisposeCount);
    }

    /// <summary>无在途 IO 时 Dispose 立即释放当前驱动。</summary>
    [Fact]
    public void Dispose_WithoutInFlightIo_ReleasesDriverImmediately()
    {
        var factory = new SwitchingDriverFactory();
        var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var driver = factory.Created.Single();

        router.Dispose();

        Assert.Equal(1, driver.DisposeCount);
    }

    /// <summary>多个并发 IO 在途时切换：所有引用归还后旧驱动才释放一次。</summary>
    [Fact]
    public async Task BrandSwitch_MultipleInFlightIos_ReleasesOldDriverOnceAllReturned()
    {
        var factory = new SwitchingDriverFactory();
        using var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var oldDriver = factory.Created.Single();

        var gate = new ManualResetEventSlim(false);
        oldDriver.BlockGate = gate;
        var task1 = Task.Run(() => router.ReadInt32("D0"));
        var task2 = Task.Run(() => router.ReadInt32("D10"));
        var task3 = Task.Run(() => router.ReadInt32("D20"));
        // 等三个 IO 都进入驱动（三次并发借用引用），期间不释放门闩
        WaitForOpenCount(oldDriver, 3);

        // 三个引用都在途：切换品牌只标记退役，不提前释放
        router.Configure(Config(PlcBrand.Siemens));
        Assert.Equal(0, oldDriver.DisposeCount);

        // 放行，等待所有 IO 完成后引用归零 → 旧驱动延迟释放
        gate.Set();
        _ = await Task.WhenAll(task1, task2, task3);

        Assert.Equal(1, oldDriver.DisposeCount);
        Assert.Equal(2, factory.Created.Count);
    }

    /// <summary>轮询等待驱动进入次数达到目标（带超时，避免测试挂死）。</summary>
    private static void WaitForOpenCount(BlockablePlcDriver driver, int target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref driver.EnterCount) < target)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"IO 未在 5s 内全部进入驱动（当前 {Volatile.Read(ref driver.EnterCount)}/{target}）");
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// P2 修复验证：同品牌 Configure 不再占用 Router 锁执行阻塞底层调用——
    /// 配置变更阻塞期间，其他线程的 IO 取引用应能立即完成（锁外执行）。
    /// </summary>
    [Fact]
    public async Task SameBrandConfigure_WithSlowConfigure_IoNotBlocked()
    {
        var factory = new SwitchingDriverFactory();
        using var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var driver = factory.Created.Single();

        // 让同品牌 Configure 阻塞（模拟底层配置变更卡在 DisconnectCore/IO）
        var configureGate = new ManualResetEventSlim(false);
        driver.ConfigureGate = configureGate;

        var slowConfig = Config(PlcBrand.Mitsubishi);
        slowConfig.Port = 5001;
        var configureTask = Task.Run(() => router.Configure(slowConfig));

        // 等 Configure 真正进入底层驱动（说明 Router 锁已释放，正在锁外执行阻塞调用）
        WaitForConfigureEntry(driver, 1);

        // 此时其他线程发起 IO：取引用不应被阻塞的 Configure 卡住，应快速完成
        var ioTask = Task.Run(() => router.ReadInt32("D0"));
        var completed = ioTask.Wait(TimeSpan.FromSeconds(2));
        Assert.True(completed, "同品牌 Configure 阻塞期间 IO 被 Router 锁阻塞（未锁外执行）");

        configureGate.Set();
        await configureTask;
        _ = await ioTask;

        Assert.Equal(5001, driver.LastConfiguredPort);
    }

    /// <summary>轮询等待 Configure 进入底层驱动（带超时）。</summary>
    private static void WaitForConfigureEntry(BlockablePlcDriver driver, int target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref driver.ConfigureEnterCount) < target)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Configure 未在 5s 内进入底层驱动（当前 {Volatile.Read(ref driver.ConfigureEnterCount)}/{target}）");
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 压力：品牌连续切换 + 在途 IO 并发 + 最终 Dispose，
    /// 验证旧驱动在引用全部归还后恰好释放一次、无泄漏、无空引用。
    /// </summary>
    [Fact]
    public async Task Stress_BrandSwitchesWithConcurrentIo_DisposeExactlyOnce()
    {
        var factory = new SwitchingDriverFactory();
        using var router = BuildRouting(factory, Config(PlcBrand.Mitsubishi));
        var firstDriver = factory.Created.Single();

        var gate = new ManualResetEventSlim(false);
        firstDriver.BlockGate = gate;

        // 并发一批 IO 全部阻塞在第一个驱动上（在途引用）
        var ioCount = 8;
        var ioTasks = Enumerable.Range(0, ioCount)
            .Select(i => Task.Run(() => router.ReadInt32($"D{i}0")))
            .ToArray();
        WaitForOpenCount(firstDriver, ioCount);

        // 第一个驱动在途期间连续切换品牌 3 次，模拟热点替换
        router.Configure(Config(PlcBrand.Siemens));
        router.Configure(Config(PlcBrand.ModbusTcp));
        router.Configure(Config(PlcBrand.Omron));

        // 全部 IO 仍阻塞在旧驱动（品牌切换只标记退役、不释放）
        Assert.Equal(0, firstDriver.DisposeCount);

        // 放行：在途 IO 完成后旧驱动被延迟释放
        gate.Set();
        _ = await Task.WhenAll(ioTasks);

        Assert.Equal(1, firstDriver.DisposeCount);
        Assert.Equal(4, factory.Created.Count); // 初始 + 3 次切换

        // 最终 Dispose 释放当前驱动
        var current = factory.Created[^1];
        router.Dispose();
        Assert.Equal(1, current.DisposeCount);

        // 验证期间每个创建的驱动都恰好释放一次（无双重释放/无泄漏）
        foreach (var driver in factory.Created)
            Assert.Equal(1, driver.DisposeCount);
    }
}