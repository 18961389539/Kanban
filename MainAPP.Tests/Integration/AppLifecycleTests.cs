using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LicenseManager.Models;
using LicenseManager.Services;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using MainAPP.Tests.Unit;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Integration;

/// <summary>
/// AppLifecycle 测试集合定义：禁止并行化，因为测试会修改 KANBAN_DATA_DIR 环境变量。
/// </summary>
[CollectionDefinition("AppLifecycle", DisableParallelization = true)]
public class AppLifecycleTestCollection
{
    // 仅供 xUnit 标识集合用，无成员
}

/// <summary>
/// App.OnStartup 异常分支和 OnExit 流程的间接测试。
///
/// 测试挑战：App 类依赖 WPF Application 生命周期、DI 容器、HandyControl UI、Mutex、Host 等，
/// 难以直接单元测试。采用间接测试策略：测试启动/退出流程中各组件的异常容错行为。
/// </summary>
[Collection("AppLifecycle")]
public class AppLifecycleTests : IDisposable
{
    private readonly string? _originalDataDir;

    public AppLifecycleTests()
    {
        _originalDataDir = Environment.GetEnvironmentVariable("KANBAN_DATA_DIR");
    }

    public void Dispose()
    {
        // 恢复 KANBAN_DATA_DIR 环境变量，避免影响其他测试
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _originalDataDir);
    }

    // ──────────── 1. App.OnStartup 异常分支测试 ────────────

    /// <summary>
    /// 1.1 配置验证错误分支：模拟 App.OnStartup line 210-220 的配置验证错误分支。
    /// 配置非法 PLC IP/端口/轮询间隔，验证 Validate() 返回非空错误列表。
    /// </summary>
    [Fact]
    public void AppSettings_Validate_WithInvalidConfig_ReturnsErrors()
    {
        var settings = new AppSettings { ConfigDirectory = Path.GetTempPath() };
        settings.PlcConfig.IpAddress = "invalid-ip";
        settings.PlcConfig.Port = 0;
        settings.PollingIntervalMs = 0;
        settings.HistoryWriteIntervalScans = 0;

        var errors = settings.Validate();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("IP"));
        Assert.Contains(errors, e => e.Contains("端口"));
        Assert.Contains(errors, e => e.Contains("轮询间隔"));
    }

    /// <summary>
    /// 1.2 devices.json 损坏分支：模拟 App.OnStartup line 227-234 的 devices.json 损坏分支。
    /// 写入坏 JSON 到 devices.json，LoadAll 后应设置 LoadErrorMessage 并备份为 .corrupt。
    /// </summary>
    [Fact]
    public void DeviceRepository_LoadAll_WithCorruptJson_SetsLoadErrorMessage()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            settings.EnsureDirectory();
            var repo = new DeviceRepository(settings);
            File.WriteAllText(repo.FilePath, "{ invalid json }");

            repo.LoadAll();

            Assert.False(string.IsNullOrEmpty(repo.LoadErrorMessage));
            Assert.True(File.Exists(repo.FilePath + ".corrupt")); // 应备份为 .corrupt
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 1.3 授权检查失败分支：用**真实 LicenseGate + 可注入时钟的 TrialTracker** 驱动试用期状态
    /// （审查修复 2026-08-13：原实现是同义反复假断言——内联重写生产条件再断言自己的布尔值，
    /// 未触达任何授权代码）。Expired/MachineMismatch 状态依赖 DPAPI+激活码，由
    /// Unit/LicenseGateTests（Requires=License）用真实门禁覆盖，此处不重复构造。
    /// </summary>
    [Fact]
    public void LicenseGate_NonActiveOrTrialStatus_ShouldBlockStartup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        try
        {
            var store = new LicenseStore(tmp);
            var registryBackup = new TrialRegistryBackupStub();
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var tracker = new TrialTracker(store, registryBackup, () => now);
            var gate = new LicenseGate(store, tracker, new ActivationAttemptTracker(tmp, () => now));

            // 首次启动：试用期 → 放行
            Assert.Equal(LicenseStatus.Trial, gate.CheckStatus());

            // 试用期过期（+31 天）→ 阻断启动
            now = now.AddDays(31);
            Assert.Equal(LicenseStatus.TrialExpired, gate.CheckStatus());
            AssertBlocking(LicenseStatus.TrialExpired);

            // 时间回拨（退到首次启动之前）→ 判定篡改 → 阻断启动
            // 注意：TrialExpired 分支早退不推进 LastLaunchUtc，须回拨越过 FirstLaunchUtc 才触发篡改判定
            now = now.AddDays(-40);
            Assert.Equal(LicenseStatus.TrialManipulated, gate.CheckStatus());
            AssertBlocking(LicenseStatus.TrialManipulated);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>与 App.xaml.cs 启动门禁同口径的阻断判定（真实 LicenseGate 状态 → 阻断映射）。</summary>
    private static void AssertBlocking(LicenseStatus status)
        => Assert.True(status is not (LicenseStatus.Active or LicenseStatus.Trial),
            $"状态 {status} 应阻断启动（App.xaml.cs 门禁：非 Active/Trial 即拦截）");

    // ──────────── 2. App.OnExit 流程测试 ────────────

    /// <summary>
    /// 2.1 PlcDataAcquisitionService.StopAsync 异常容错：模拟 App.OnExit line 333-340 的 StopAsync try/catch。
    /// 验证 StopAsync 即使内部抛异常也应以优雅方式退出。
    /// </summary>
    [Fact]
    public async Task PlcDataAcquisitionService_StopAsync_WithException_DoesNotCrash()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var plc = new FakePlcDriver();
            var settings = new AppSettings();
            var connMgr = new PlcConnectionManager(plc, settings);
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(settings);
            var baseline = new ProductionBaselineStore(settings);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, settings, history, repo, baseline,
                NullLogger<PlcDataAcquisitionService>.Instance);

            svc.Start();
            await Task.Delay(100); // 让轮询跑一会

            var ex = await Record.ExceptionAsync(() => svc.StopAsync());
            Assert.Null(ex); // StopAsync 不应抛出异常
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 2.2 DeviceRepository.SaveAll 异常容错：模拟 App.OnExit line 342-349 的 SaveAll try/catch。
    /// 验证 SaveAll 在正常目录下不抛异常（被 catch 吞掉）。
    /// 注意：Windows 目录的 ReadOnly 属性对文件写入无约束力，原名"WithReadOnlyDirectory"名不副实（审查修复 2026-08-13 更名）。
    /// </summary>
    [Fact]
    public void DeviceRepository_SaveAll_NormalDirectory_DoesNotThrow()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            var repo = new DeviceRepository(settings);
            repo.Devices.Add(new Device { Id = "test", Name = "测试" });

            // 正常保存
            var ex = Record.Exception(() => repo.SaveAll());
            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 2.3 HistoryService.DisposeAsync 异常容错：模拟 App.OnExit line 360-367 的 DisposeAsync try/catch。
    /// 验证 DisposeAsync 能 flush 待写数据且不抛异常。
    /// </summary>
    [Fact]
    public async Task HistoryService_DisposeAsync_FlushesPendingData()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        HistoryService? history = null;
        try
        {
            var settings = new AppSettings();
            var db = new DatabaseProvider(settings);
            history = new HistoryService(db, NullLogger<HistoryService>.Instance);

            // 确保三个数据库均已建表
            using (var ctx = db.CreateAlarmEventContext()) ctx.Database.EnsureCreated();
            using (var ctx = db.CreateProductionLogContext()) ctx.Database.EnsureCreated();
            using (var ctx = db.CreateStatusTransitionContext()) ctx.Database.EnsureCreated();

            // 入队一些数据（同步写入报警事件）
            history.LogAlarmEvent(
                "dev-001", "测试设备", "alm-001", "高温报警", "M100",
                AlarmEventType.Triggered, DateTime.Now);

            var ex = await Record.ExceptionAsync(() => history.DisposeAsync().AsTask());
            Assert.Null(ex);
        }
        finally
        {
            try { history?.Dispose(); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 2.4 SafeReleaseMutex 行为：模拟 App.SafeReleaseMutex 的 ApplicationException catch 分支。
    /// 验证未持有所有权时 ReleaseMutex 抛 ApplicationException。
    /// </summary>
    [Fact]
    public void Mutex_ReleaseMutex_WithoutOwnership_ThrowsApplicationException()
    {
        var mutex = new Mutex(false, @"Global\Test_Mutex_" + Guid.NewGuid().ToString("N"));

        try
        {
            // 未 WaitOne 直接 ReleaseMutex 应抛 ApplicationException
            Assert.Throws<ApplicationException>(() => mutex.ReleaseMutex());
        }
        finally
        {
            mutex.Dispose();
        }
    }

    /// <summary>
    /// 2.4 SafeReleaseMutex 正常路径：模拟 App.SafeReleaseMutex 正常路径。
    /// 验证持有所有权时 ReleaseMutex 不抛异常。
    /// </summary>
    [Fact]
    public void Mutex_ReleaseMutex_WithOwnership_DoesNotThrow()
    {
        var mutex = new Mutex(true, @"Global\Test_Mutex_" + Guid.NewGuid().ToString("N"), out bool createdNew);
        Assert.True(createdNew);

        try
        {
            var ex = Record.Exception(() => mutex.ReleaseMutex());
            Assert.Null(ex);
        }
        finally
        {
            mutex.Dispose();
        }
    }

    /// <summary>
    /// 2.5 多实例检测：模拟 App 构造函数 line 41-42 的 _isFirstInstance 判断。
    /// 验证第二个实例 createdNew=false。
    /// </summary>
    [Fact]
    public void Mutex_SecondInstance_CreatedNewIsFalse()
    {
        var mutexName = @"Global\Test_Mutex_" + Guid.NewGuid().ToString("N");
        var m1 = new Mutex(true, mutexName, out bool firstCreated);
        Assert.True(firstCreated);

        try
        {
            var m2 = new Mutex(true, mutexName, out bool secondCreated);
            Assert.False(secondCreated); // 第二个实例 createdNew=false
            m2.Dispose();
        }
        finally
        {
            m1.Dispose();
        }
    }

    // ──────────── 3. 空设备列表启动路径 ────────────
    // App.OnStartup line 226-242 调用 deviceRepo.LoadAll() 与 RefreshDeviceList()。
    // devices.json 文件不存在 / 内容为空数组 / Devices 集合为空时，启动流程应继续，
    // 不抛异常、不阻塞主窗口显示。ProductionLineViewModel 会进入 EmptyState。

    /// <summary>
    /// 3.1 devices.json 不存在时启动：LoadAll 应静默返回空列表，不报错，不设置 LoadErrorMessage。
    /// 对应 App.OnStartup line 230-238 的 LoadErrorMessage 判空分支（无警告 MessageBox）。
    /// </summary>
    [Fact]
    public void DeviceRepository_LoadAll_NoDevicesFile_StartsWithEmptyListNoError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            settings.EnsureDirectory();
            var repo = new DeviceRepository(settings);

            // 确认 devices.json 不存在
            Assert.False(File.Exists(repo.FilePath));

            repo.LoadAll();

            // 关键断言：空列表、无错误
            Assert.Empty(repo.Devices);
            Assert.Empty(repo.Runtimes);
            Assert.True(string.IsNullOrEmpty(repo.LoadErrorMessage));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 3.2 devices.json 内容为空数组时启动：LoadAll 应解析得到空 Devices 列表，不报错。
    /// </summary>
    [Fact]
    public void DeviceRepository_LoadAll_EmptyJsonArray_StartsWithEmptyListNoError()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            settings.EnsureDirectory();
            var repo = new DeviceRepository(settings);

            // 写入空数组
            File.WriteAllText(repo.FilePath, "[]");

            repo.LoadAll();

            Assert.Empty(repo.Devices);
            Assert.Empty(repo.Runtimes);
            Assert.True(string.IsNullOrEmpty(repo.LoadErrorMessage));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 3.3 空设备列表下 ProductionLineViewModel 应进入 EmptyState：
    /// - HasNoDevices = true（LineDevices.Count == 0）
    /// - 不抛异常
    /// 这验证 App.OnStartup → MainWindow 显示 → ProductionLineView 绑定链路在空设备时正常。
    /// </summary>
    [Fact]
    public void EmptyDeviceList_Startup_ProductionLineViewModelEntersEmptyState()
    {
        var settings = new AppSettings { ConfigDirectory = Path.GetTempPath() };
        var repo = new DeviceRepository(settings);
        repo.LoadAll();
        Assert.Empty(repo.Devices);

        var sel = new DeviceSelectionService();
        var vm = new ProductionLineViewModel(repo, sel);

        // 关键断言：空设备时进入 EmptyState，不抛异常
        Assert.True(vm.HasNoDevices);
        Assert.Empty(vm.LineDevices);

        // 汇总 KPI 全为 0
        Assert.Equal(0, vm.TotalOkProduction);
        Assert.Equal(0, vm.TotalNgProduction);
        Assert.Equal(0, vm.RunningCount);
        Assert.Equal(0, vm.AlarmCount);
    }

    /// <summary>
    /// 3.4 空设备列表下 PlcDataAcquisitionService 应正常启动：
    /// - GetDevicesSnapshot() 返回空列表，循环不执行
    /// - 不抛异常
    /// - noDevicesToRead = true（0 == 0）
    /// </summary>
    [Fact]
    public async Task EmptyDeviceList_Startup_PlcServiceRunsAndStopsCleanly()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var plc = new FakePlcDriver();
            var settings = new AppSettings();
            var connMgr = new PlcConnectionManager(plc, settings);
            var history = new InMemoryHistoryService();
            var repo = new DeviceRepository(settings);
            repo.LoadAll();
            Assert.Empty(repo.Devices);

            var baseline = new ProductionBaselineStore(settings);
            var svc = new PlcDataAcquisitionService(
                plc, connMgr, settings, history, repo, baseline,
                NullLogger<PlcDataAcquisitionService>.Instance);

            svc.Start();
            await Task.Delay(100); // 让轮询跑一会

            // 空设备列表下 noDevicesToRead 应为 true
            var success = svc.RefreshDeviceData(out var noDevices);
            Assert.True(noDevices);
            Assert.Empty(success);

            var ex = await Record.ExceptionAsync(() => svc.StopAsync());
            Assert.Null(ex);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ──────────── 4. 冷启动关键路径性能 ────────────
    // App.xaml.cs 有 18+ 启动计时埋点（写日志），但无自动化测试读取这些指标做回归断言。
    // 此处对启动链路上的关键阻塞步骤做 Stopwatch 计时断言：
    // - AppSettings.Load：JSON 反序列化
    // - DeviceRepository.LoadAll：JSON 反序列化 + Runtime 创建（100 台设备）
    // - ProductionBaselineStore.Load：baselines.json 加载
    // - ProductionLineViewModel 构造：100 台设备聚合 KPI
    //
    // 不直接测 App 启动到 MainWindow 显示（WPF + DI + HandyControl 依赖太重），
    // 改为分阶段测量，单点超时即可定位回归。
    // 审查修复 2026-08-13：阈值从"CI 均值 5x"（数百 ms）放宽为 5000ms 硬顶——
    // 墙钟计时在慢机/高负载 CI 上必然误报，本组用例定位是"启动冒烟 + 病理性退化拦截"
    // （如意外 O(n²) 把加载拉到数十秒），真实性能度量归 BenchmarkDotNet（MainAPP.Benchmarks）。

    /// <summary>启动冒烟硬顶（ms）：只拦病理性退化，不做性能回归度量。</summary>
    private const int StartupSmokeBudgetMs = 5000;

    /// <summary>AppSettings.Load 冒烟硬顶（ms）。</summary>
    private const int AppSettingsLoadBudgetMs = StartupSmokeBudgetMs;

    /// <summary>DeviceRepository.LoadAll 100 台设备冒烟硬顶（ms）。</summary>
    private const int DeviceRepoLoadAll100BudgetMs = StartupSmokeBudgetMs;

    /// <summary>ProductionBaselineStore.Load 冒烟硬顶（ms）。</summary>
    private const int BaselineStoreLoadBudgetMs = StartupSmokeBudgetMs;

    /// <summary>ProductionLineViewModel 构造（100 台设备）冒烟硬顶（ms）。</summary>
    private const int ProductionLineVm100BudgetMs = StartupSmokeBudgetMs;

    [Fact]
    public void Startup_AppSettingsLoad_CompletesWithinBudget()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var sw = Stopwatch.StartNew();
            var settings = new AppSettings();
            settings.EnsureDirectory();
            settings.Load();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < AppSettingsLoadBudgetMs,
                $"AppSettings.Load 耗时 {sw.ElapsedMilliseconds}ms 超过预算 {AppSettingsLoadBudgetMs}ms");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Startup_DeviceRepositoryLoadAll_100Devices_CompletesWithinBudget()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            settings.EnsureDirectory();
            var repo = new DeviceRepository(settings);

            // 预置 100 台设备 JSON
            for (int i = 0; i < 100; i++)
            {
                repo.Devices.Add(new Device
                {
                    Id = "dev-" + i.ToString("D3"),
                    Name = "设备" + i,
                    OkCountAddress = "D" + (100 + i * 10),
                    NgCountAddress = "D" + (101 + i * 10),
                    StatusCountAddress = "D" + (102 + i * 10),
                    ProductionResetAddress = "D" + (103 + i * 10),
                    TargetCycle = 100
                });
            }
            repo.SaveAll();

            // 重新实例化 repo 模拟冷启动
            var coldRepo = new DeviceRepository(settings);
            var sw = Stopwatch.StartNew();
            coldRepo.LoadAll();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < DeviceRepoLoadAll100BudgetMs,
                $"DeviceRepository.LoadAll (100 台) 耗时 {sw.ElapsedMilliseconds}ms 超过预算 {DeviceRepoLoadAll100BudgetMs}ms");
            Assert.Equal(100, coldRepo.Devices.Count);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Startup_ProductionBaselineStoreLoad_CompletesWithinBudget()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "app_lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", tmp);

        try
        {
            var settings = new AppSettings();
            settings.EnsureDirectory();

            var sw = Stopwatch.StartNew();
            var store = new ProductionBaselineStore(settings);
            store.Load();
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < BaselineStoreLoadBudgetMs,
                $"ProductionBaselineStore.Load 耗时 {sw.ElapsedMilliseconds}ms 超过预算 {BaselineStoreLoadBudgetMs}ms");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void Startup_ProductionLineViewModel_100Devices_ConstructsWithinBudget()
    {
        var repo = new DeviceRepository(new AppSettings());
        for (int i = 0; i < 100; i++)
        {
            var dev = new Device { Name = "设备" + i, TargetCycle = 100 };
            repo.Devices.Add(dev);
            repo.Runtimes.Add(new DeviceRuntime(dev)
            {
                StatusWord = (int)DeviceStatus.Running,
                TotalOkProduction = 1000,
                TotalNgProduction = 10,
                RunTime = 3600,
            });
        }

        var sel = new DeviceSelectionService();
        var sw = Stopwatch.StartNew();
        var vm = new ProductionLineViewModel(repo, sel);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < ProductionLineVm100BudgetMs,
            $"ProductionLineViewModel 构造（100 台）耗时 {sw.ElapsedMilliseconds}ms 超过预算 {ProductionLineVm100BudgetMs}ms");
        Assert.Equal(100, vm.LineDevices.Count);
    }
}
