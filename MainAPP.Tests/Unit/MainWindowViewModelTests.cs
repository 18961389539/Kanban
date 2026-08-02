using System;
using System.IO;
using LicenseManager.Services;
using MainAPP.Data;
using MainAPP.Services;
using MainAPP.Tests;
using MainAPP.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MainAPP.Tests.Unit;

/// <summary>
/// MainWindowViewModel 单元测试：覆盖构造初始化、导航项 UIA 可访问性、
/// 侧边栏折叠命令、跳转授权设置命令、以及产线/概览页跳转主页事件。
/// 因构造 LicenseGate/AppSettings 需临时目录与 KANBAN_DATA_DIR 环境变量，关闭并行化以避免相互干扰。
/// </summary>
[CollectionDefinition("MainWindowVM", DisableParallelization = true)]
[Trait("Category","Unit")]
[Trait("Speed","Fast")]
[Trait("Requires","None")]
public class MainWindowVMCollectionDefinition { }

[Collection("MainWindowVM")]
public class MainWindowViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _appSettings;
    private readonly FakePlcDriver _driver;
    private readonly PlcConnectionManager _conn;
    private readonly FakeDialogService _dialog;
    private readonly DeviceRepository _deviceRepo;
    private readonly InMemoryHistoryService _historyService;
    private readonly IDeviceSelectionService _selection;
    private readonly LicenseGate _licenseGate;
    private readonly IServiceProvider _services;

    // DeviceManagerViewModel 的协作服务（构造时一次性创建，供每次 NewVm 复用）
    private readonly PlcDataAcquisitionService _dataAcq;
    private readonly DeviceConfigIOService _configIO;
    private readonly DevicePlcCommandHandler _plcCommands;
    private readonly DatabaseProvider _dbProvider;
    private readonly WorkOrderRepository _workOrderRepo;

    public MainWindowViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kanban_mainwindowvm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Config")); // ProductionBaselineStore 写 baselines.json 到 Config 子目录
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", _tempDir);

        _appSettings = new AppSettings();
        _deviceRepo = new DeviceRepository(_appSettings);
        _driver = new FakePlcDriver();
        _conn = new PlcConnectionManager(_driver, _appSettings);
        _historyService = new InMemoryHistoryService();
        _dialog = new FakeDialogService();
        _selection = new DeviceSelectionService();

        // 授权门禁：用临时目录 + 内存版 RegistryBackup，避免污染测试机器注册表
        var licenseStore = new LicenseStore(_tempDir);
        var registryBackup = new TrialRegistryBackupStub();
        var trialTracker = new TrialTracker(licenseStore, registryBackup);
        var attemptTracker = new ActivationAttemptTracker(_tempDir);
        _licenseGate = new LicenseGate(licenseStore, trialTracker, attemptTracker);
        _licenseGate.CheckStatus(); // 初始化状态（首次启动 → 试用期）

        _services = new ServiceCollection().BuildServiceProvider();

        // 构造 DeviceManagerViewModel 的协作服务
        var baselineStore = new ProductionBaselineStore(_appSettings);
        _dataAcq = new PlcDataAcquisitionService(
            _driver, _conn, _appSettings, _historyService, _deviceRepo, baselineStore,
            NullLogger<PlcDataAcquisitionService>.Instance);
        _configIO = new DeviceConfigIOService(_deviceRepo, _dialog);
        _plcCommands = new DevicePlcCommandHandler(_driver, _conn, _dataAcq);

        _dbProvider = new DatabaseProvider(_appSettings);
        _dbProvider.EnsureCreatedAll();
        _workOrderRepo = new WorkOrderRepository(_dbProvider, TestMapper.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
        Environment.SetEnvironmentVariable("KANBAN_DATA_DIR", null);
    }

    /// <summary>
    /// 构造被测对象：所有依赖 ViewModel 都用真实实现构造，确保构造函数订阅事件路径被覆盖。
    /// </summary>
    private MainWindowViewModel NewVm()
    {
        var alarmCsvIO = new AlarmCsvIOService(_dialog);
        var workOrderService = new WorkOrderService(_workOrderRepo, _deviceRepo, _dialog, _historyService);
        var deviceManagerVm = new DeviceManagerViewModel(_deviceRepo, _dataAcq, _dialog, _configIO, _plcCommands, alarmCsvIO, _workOrderRepo, workOrderService);
        var historyQueryVm = new HistoryQueryViewModel(_historyService, _deviceRepo, _appSettings, _dialog);
        var homeVm = new HomeViewModel(_deviceRepo, _conn, _appSettings, null!, _selection);
        var productionLineVm = new ProductionLineViewModel(_deviceRepo, _selection, null, _appSettings);
        var alarmCenterVm = new AlarmCenterViewModel(_historyService, _deviceRepo, _dialog);
        var overviewVm = new OverviewViewModel(_historyService, _deviceRepo, _appSettings, _dialog, _selection);
        var deviceDetailVm = new DeviceDetailViewModel(_deviceRepo, _historyService, _selection, _dialog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceDetailViewModel>.Instance,
            _workOrderRepo, workOrderService);
        var settingsVm = new SettingsViewModel(_appSettings, _conn, _dialog, _licenseGate, _services);
        var workOrderVm = new WorkOrderManagerViewModel(_workOrderRepo, workOrderService, _deviceRepo, _dialog);
        return new MainWindowViewModel(
            _appSettings, deviceManagerVm, historyQueryVm, homeVm,
            productionLineVm, alarmCenterVm, overviewVm, deviceDetailVm, workOrderVm, settingsVm, _conn, _licenseGate);
    }

    // ───────────── 构造函数初始化 ─────────────

    [Fact]
    public void Constructor_InitializesAllProperties()
    {
        var vm = NewVm();

        Assert.Equal(_appSettings, vm.AppSettings);
        Assert.NotNull(vm.DeviceManagerViewModel);
        Assert.NotNull(vm.HistoryQueryViewModel);
        Assert.NotNull(vm.HomeViewModel);
        Assert.NotNull(vm.ProductionLineViewModel);
        Assert.NotNull(vm.OverviewViewModel);
        Assert.NotNull(vm.SettingsViewModel);
        Assert.Equal(_conn, vm.ConnectionManager);
        Assert.Equal(_licenseGate, vm.LicenseGate);
        Assert.NotNull(vm.NavItems);
    }

    [Fact]
    public void NavItems_HasNineItems()
    {
        var vm = NewVm();
        // 主页/产线/报警中心/设备管理/工单/历史查询/生产复盘/设置/运行监控 共 9 项
        // 设备详情页是上下文页面，不作为侧边栏常驻项（入口在主页"查看详情"按钮）
        Assert.Equal(9, vm.NavItems.Count);
    }

    [Fact]
    public void NavItems_AllHaveAccessibleName()
    {
        var vm = NewVm();

        // 每个导航项 AccessibleName 不能为空：UIA 客户端（FlaUI/WinAppDriver）通过 ByName 定位并点击
        Assert.All(vm.NavItems, item => Assert.False(string.IsNullOrWhiteSpace(item.AccessibleName)));
    }

    [Fact]
    public void SelectedIndex_DefaultZero()
    {
        var vm = NewVm();

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void IsSidebarCollapsed_DefaultFalse()
    {
        var vm = NewVm();

        // 默认展开，配置员首次进入即可看到页面名称
        Assert.False(vm.IsSidebarCollapsed);
    }

    // ───────────── 命令 ─────────────

    [Fact]
    public void ToggleSidebarCommand_TogglesCollapseState()
    {
        var vm = NewVm();
        Assert.False(vm.IsSidebarCollapsed); // 初始 false

        vm.ToggleSidebarCommand.Execute(null);
        Assert.True(vm.IsSidebarCollapsed); // 切换为 true

        vm.ToggleSidebarCommand.Execute(null);
        Assert.False(vm.IsSidebarCollapsed); // 再次切换回 false
    }

    [Fact]
    public void GoToLicenseSettingsCommand_SetsSelectedIndexToSeven()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.SelectedIndex); // 前置：默认在主页

        // 授权状态卡片点击：跳转到设置页（索引 7）
        vm.GoToLicenseSettingsCommand.Execute(null);

        Assert.Equal(7, vm.SelectedIndex);
    }

    // ───────────── 跨页跳转事件 ─────────────

    [Fact]
    public void ProductionLineViewModel_FocusDeviceRequested_SetsSelectedIndexZero()
    {
        var vm = NewVm();
        vm.SelectedIndex = 1; // 模拟当前停留在产线页

        // 产线页"跳转主页"请求：通过 FocusDeviceCommand 触发 FocusDeviceRequested 事件，
        // MainWindowViewModel 订阅后置 SelectedIndex=0
        vm.ProductionLineViewModel.FocusDeviceCommand.Execute("any-device-id");

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void OverviewViewModel_FocusDeviceRequested_SetsSelectedIndexZero()
    {
        var vm = NewVm();
        vm.SelectedIndex = 5; // 模拟当前停留在概览页（索引 5 = 生产复盘）

        // 概览页"跳转主页"请求：通过 FocusDeviceCommand 触发 FocusDeviceRequested 事件，
        // MainWindowViewModel 订阅后置 SelectedIndex=0
        vm.OverviewViewModel.FocusDeviceCommand.Execute("any-device-id");

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void HomeViewModel_ViewDeviceDetailRequested_SetsSelectedIndexNine()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.SelectedIndex); // 前置：默认在主页

        // 设备未配置时 SelectedDeviceId 为 null，ViewDeviceDetailCommand.CanExecute 返回 false。
        // 直接设置 SelectedDeviceId 模拟用户选中设备，使命令可执行。
        vm.HomeViewModel.SelectedDeviceId = "any-device-id";
        Assert.True(vm.HomeViewModel.ViewDeviceDetailCommand.CanExecute(null));

        // 主页"查看设备详情"请求：通过 ViewDeviceDetailCommand 触发 ViewDeviceDetailRequested 事件，
        // MainWindowViewModel 订阅后置 SelectedIndex=9（设备详情页上下文索引）
        vm.HomeViewModel.ViewDeviceDetailCommand.Execute(null);

        Assert.Equal(9, vm.SelectedIndex);
    }
}
