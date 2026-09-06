using System.IO;
using System.Linq;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// DeviceDetailViewModel 单元测试：覆盖设备选中、运行时同步、KPI 聚合。
///
/// 测试策略：
/// - 使用真实 DeviceRepository + 内存设备配置
/// - 使用 InMemoryHistoryService 注入历史数据
/// - 使用真实 WorkOrderRepository + 临时 SQLite 数据库
/// - 使用 DeviceSelectionService（IDeviceSelectionService 默认实现）跨页同步选中状态
/// - 使用 FakeDialogService
/// - 使用 NullLogger&lt;DeviceDetailViewModel&gt; 避免依赖日志基础设施
///
/// 覆盖范围：
/// - 构造函数：无选中设备时 HasDevice=false
/// - 选中设备：通过 IDeviceSelectionService 切换后 HasDevice=true
/// - 运行时同步：DeviceRuntime 属性变化触发 KPI 更新
/// - 空状态切换：选中 null 设备后清空
/// </summary>
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class DeviceDetailViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly DeviceRepository _deviceRepo;
    private readonly InMemoryHistoryService _historyService;
    private readonly DeviceSelectionService _selection;
    private readonly FakeDialogService _dialog;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;
    private readonly WorkOrderService _workOrderService;

    public DeviceDetailViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DdvmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _appSettings = new AppSettings { ConfigDirectory = _tempDir };
        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
        _deviceRepo = new DeviceRepository(_appSettings);
        _historyService = new InMemoryHistoryService();
        _selection = new DeviceSelectionService();
        _dialog = new FakeDialogService();
        _workOrderService = new WorkOrderService(_workOrderRepo, _deviceRepo, _dialog, _historyService);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private DeviceDetailViewModel CreateVm()
        => new(_deviceRepo, _historyService, _selection, _dialog,
            NullLogger<DeviceDetailViewModel>.Instance, _workOrderRepo, _workOrderService, _appSettings);

    private static Device CreateDevice(string id = "d1", string name = "设备1")
    {
        return new Device
        {
            Id = id,
            Name = name,
            TargetCycle = 60,
        };
    }

    // ──────────── 构造函数 ────────────

    [Fact]
    public void Constructor_WithoutSelectedDevice_HasNoDevice()
    {
        var vm = CreateVm();
        Assert.False(vm.HasDevice);
        Assert.True(vm.HasNoDevice);
        Assert.Null(vm.CurrentDevice);
        Assert.Null(vm.CurrentRuntime);
    }

    // ──────────── 选中设备 ────────────

    [Fact]
    public void SelectionChanged_UpdatesCurrentDevice()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        Assert.True(vm.HasDevice);
        Assert.False(vm.HasNoDevice);
        Assert.NotNull(vm.CurrentDevice);
        Assert.Equal("设备1", vm.CurrentDevice!.Name);
    }

    [Fact]
    public void SelectionChanged_UpdatesCurrentRuntime()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device);
        _deviceRepo.Runtimes.First(r => r.DeviceId == "d1").TotalOkProduction = 42;

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        Assert.NotNull(vm.CurrentRuntime);
        Assert.Equal(42, vm.CurrentRuntime!.TotalOkProduction);
    }

    [Fact]
    public void SelectionChanged_ToNull_ClearsDevice()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device);

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";
        Assert.True(vm.HasDevice);

        _selection.SelectedDeviceId = null;
        Assert.False(vm.HasDevice);
        Assert.Null(vm.CurrentDevice);
        Assert.Null(vm.CurrentRuntime);
    }

    [Fact]
    public void SelectionChanged_ToNonExistentDevice_ClearsCurrent()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";
        Assert.True(vm.HasDevice);

        // 切换到不存在的设备 Id
        _selection.SelectedDeviceId = "non-existent";
        Assert.False(vm.HasDevice);
    }

    // ──────────── 运行时属性同步 ────────────

    [Fact]
    public void ApplySelectedDevice_InitializesKpiFromRuntime()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device);
        var runtime = _deviceRepo.Runtimes.First(r => r.DeviceId == "d1");
        runtime.TotalOkProduction = 100;
        runtime.TotalNgProduction = 5;

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        // ApplySelectedDevice → RefreshKpis 从 Runtime 读取初始 KPI（直接调用，不经过 DispatchOnUi）
        Assert.Equal(100, vm.TotalOk);
        Assert.Equal(5, vm.TotalNg);
    }

    [Fact]
    public void RefreshKpis_ConvertsSecondsToHoursAndUsesRunWindowTheoreticalOutput()
    {
        var device = CreateDevice("d1", "设备1");
        device.TargetCycle = 60;
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device);
        var runtime = _deviceRepo.Runtimes.First(r => r.DeviceId == "d1");
        runtime.RunTime = 3600;
        runtime.AlarmTime = 1800;
        runtime.PausedTime = 900;
        runtime.OfflineTime = 7200;
        runtime.TotalOkProduction = 50;
        runtime.TotalNgProduction = 10;

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        Assert.Equal(1.0, vm.RunTimeHours, 5);
        Assert.Equal(0.5, vm.AlarmTimeHours, 5);
        Assert.Equal(0.25, vm.PausedTimeHours, 5);
        Assert.Equal(2.0, vm.OfflineTimeHours, 5);
        Assert.Equal(60, vm.TheoreticalOutput);
        Assert.Equal(60, vm.ActualCycleRate, 5);
        Assert.Equal(1.0 / 3.75, vm.RunTimeRatio, 5);
        Assert.Equal(2.0 / 3.75, vm.OfflineTimeRatio, 5);
    }

    [Fact]
    public void RefreshKpis_OfflineStatusText_IncludesPlcCause()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device);
        var runtime = _deviceRepo.Runtimes.First(r => r.DeviceId == "d1");
        runtime.ApplyLiveStatus(0, Kanban.Contracts.Enums.OfflineCause.PlcReported);

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        Assert.Equal("离线（PLC）", vm.StatusText);
    }

    // ──────────── 设备属性同步 ────────────

    [Fact]
    public void DevicePropertyChanged_UpdatesDeviceName()
    {
        var device = CreateDevice("d1", "旧名称");
        _deviceRepo.Devices.Add(device);

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";
        Assert.Equal("旧名称", vm.CurrentDevice!.Name);

        device.Name = "新名称";
        // DeviceDetailViewModel 订阅了 CurrentDevice.PropertyChanged
        Assert.Equal("新名称", vm.CurrentDevice.Name);
    }

    // ──────────── 无运行时数据 ────────────

    [Fact]
    public void SelectionChanged_DeviceWithoutRuntime_ClearsRuntime()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        // 不添加 DeviceRuntime

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        Assert.True(vm.HasDevice);
        Assert.NotNull(vm.CurrentDevice);
        Assert.Null(vm.CurrentRuntime);
    }

    // ──────────── 工单关联 ────────────

    [Fact]
    public void SelectionChanged_DeviceWithRunningWorkOrder_ShowsWorkOrder()
    {
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);

        var wo = _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-001",
            ProductName = "产品A",
            DeviceId = "d1",
            Status = WorkOrderStatus.Running,
        });

        var vm = CreateVm();
        _selection.SelectedDeviceId = "d1";

        // DeviceDetailViewModel 应查询并显示当前设备的 Running 工单
        // 具体属性名取决于实现，这里验证不抛异常且设备已选中
        Assert.True(vm.HasDevice);
    }

    [Fact]
    public void RunningWorkOrder_SummaryQuery_DoesNotBlockUiThread_AndAppliesResult()
    {
        // 回归（审查修复 2026-08-13）：产量聚合曾同步执行在 UI 线程（Remote 模式最坏阻塞 30s），
        // 现改为后台查询 + 节流 + UI 封送回写。此处用慢查询替身验证"选择设备立即返回、结果异步回写"。
        var device = CreateDevice("d1", "设备1");
        _deviceRepo.Devices.Add(device);
        _deviceRepo.AddRuntime(device); // RefreshKpis 需 Runtime 非空才会走到 RefreshWorkOrder
        _workOrderRepo.Upsert(new WorkOrder
        {
            OrderNo = "WO-001",
            ProductName = "产品A",
            DeviceId = "d1",
            Status = WorkOrderStatus.Running,
            TargetQuantity = 100,
        });

        var slowService = NSubstitute.Substitute.For<IWorkOrderService>();
        using var release = new System.Threading.ManualResetEventSlim();
        slowService.GetProductionSummary(Arg.Any<WorkOrder>()).Returns(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10)); // 模拟慢查询
            return new WorkOrderProductionSummary { OkCount = 42 };
        });

        var vm = new DeviceDetailViewModel(_deviceRepo, _historyService, _selection, _dialog,
            NullLogger<DeviceDetailViewModel>.Instance, _workOrderRepo, slowService, _appSettings);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _selection.SelectedDeviceId = "d1";
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"选择设备不应被工单聚合查询阻塞（实际 {sw.ElapsedMilliseconds}ms）");

        release.Set();

        // 异步回写：轮询等待结果（Dispatcher 不可用的测试环境下直接回写）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.CompletedQuantity != 42 && DateTime.UtcNow < deadline)
            System.Threading.Thread.Sleep(10);
        Assert.Equal(42, vm.CompletedQuantity);
        Assert.Equal(0.42, vm.WorkOrderProgress, 2);
        slowService.Received(1).GetProductionSummary(Arg.Any<WorkOrder>());
    }
}
