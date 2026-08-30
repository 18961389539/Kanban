using System;
using System.IO;
using System.Windows;
using LicenseManager.Services;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Models;
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
    /// 使用已登录 Admin 角色的 UserSession，确保所有受权限保护的页面（设置/运行监控/设备管理/用户管理）可访问。
    /// </summary>
    private MainWindowViewModel NewVm(UserSession? session = null, ILoginDialogService? loginDialogService = null)
    {
        var userSession = session ?? new UserSession();
        if (!userSession.IsLoggedIn)
            userSession.Login(new User { Username = "admin", DisplayName = "管理员", Role = UserRole.Admin });

        var alarmCsvIO = new AlarmCsvIOService(_dialog);
        var defectCsvIO = new DefectCsvIOService(_dialog);
        var counterAlarmCsvIO = new CounterAlarmCsvIOService(_dialog);
        var dataSourceCsvIO = new DataSourceCsvIOService(_dialog);
        var workOrderService = new WorkOrderService(_workOrderRepo, _deviceRepo, _dialog, _historyService);
        var deviceManagerVm = new DeviceManagerViewModel(_deviceRepo, _dataAcq, _dialog, _configIO, _plcCommands, alarmCsvIO, defectCsvIO, counterAlarmCsvIO, dataSourceCsvIO, _workOrderRepo, workOrderService, userSession);
        var historyQueryVm = new HistoryQueryViewModel(_historyService, _deviceRepo, _appSettings, _dialog);
        var homeVm = new HomeViewModel(_deviceRepo, _conn, _appSettings, null!, _selection);
        var productionLineVm = new ProductionLineViewModel(_deviceRepo, _selection, null, _appSettings);
        var alarmCenterVm = new AlarmCenterViewModel(_historyService, _deviceRepo, _dialog);
        var overviewVm = new OverviewViewModel(_historyService, _deviceRepo, _appSettings, _dialog, _selection);
        var deviceDetailVm = new DeviceDetailViewModel(_deviceRepo, _historyService, _selection, _dialog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceDetailViewModel>.Instance,
            _workOrderRepo, workOrderService);
        var settingsVm = new SettingsViewModel(_appSettings, _conn, _dialog, _licenseGate, _services);
        var workOrderVm = new WorkOrderManagerViewModel(_workOrderRepo, workOrderService, _deviceRepo, _dialog, userSession);
        // 页面 VM 懒加载（2026-08-11）：MainWindowViewModel 经 IServiceProvider 惰性解析页面 VM，
        // 测试把预先构造的真实 VM 实例注册进临时容器，属性首次访问时解析。
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(deviceManagerVm);
        services.AddSingleton(historyQueryVm);
        services.AddSingleton(homeVm);
        services.AddSingleton(productionLineVm);
        services.AddSingleton(alarmCenterVm);
        services.AddSingleton(overviewVm);
        services.AddSingleton(deviceDetailVm);
        services.AddSingleton(workOrderVm);
        services.AddSingleton(settingsVm);
        return new MainWindowViewModel(
            _appSettings, services.BuildServiceProvider(),
            _conn, _licenseGate, userSession, loginDialogService: loginDialogService);
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
    public void NavItems_HasAllSidebarItems()
    {
        var vm = NewVm();
        // 主页/产线/报警中心/设备管理/工单/历史查询/生产复盘/设置/运行监控/用户管理/审计日志/配方管理/采集监控 共 13 项
        // 设备详情页是上下文页面，不作为侧边栏常驻项（入口在主页"查看详情"按钮）
        Assert.Equal(13, vm.NavItems.Count);
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

    [Fact]
    public void SelectPageCommand_OperatorRole_BlocksGatedPages_AllowsOpenPages()
    {
        // 回归（审查修复 2026-08-13）：Ctrl+1~8 快捷键必须走与 Navigate 相同的角色门禁，
        // Operator 不能直达设备管理（Engineer）/设置、运行监控（Admin）。
        var session = new UserSession();
        session.Login(new User { Username = "operator", DisplayName = "操作员", Role = UserRole.Operator });
        var vm = NewVm(session);
        vm.SelectedIndex = NavigationPageCatalog.Home.Index;

        vm.SelectPageCommand.Execute("3"); // 设备管理 → 被拦
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        vm.SelectPageCommand.Execute("7"); // 设置 → 被拦
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        vm.SelectPageCommand.Execute("8"); // 运行监控 → 被拦
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);

        vm.SelectPageCommand.Execute("1"); // 产线（无角色要求）→ 放行
        Assert.Equal(NavigationPageCatalog.ProductionLine.Index, vm.SelectedIndex);
    }

    [Fact]
    public void SelectPageCommand_AdminRole_NavigatesByShortcut()
    {
        var vm = NewVm(); // 默认 Admin 会话

        vm.SelectPageCommand.Execute("3");

        Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);
    }

    [Fact]
    public void SelectPageCommand_ViewerMode_BlocksNonWhitelistedPages()
    {
        // 回归（审查修复 2026-08-13）：Viewer 模式白名单 = Home/ProductionLine/AlarmCenter/DeviceDetail，
        // 快捷键不能绕过白名单进入其他页面。
        _appSettings.RunMode = KanbanRunMode.Viewer;
        var vm = NewVm();
        vm.SelectedIndex = NavigationPageCatalog.Home.Index;

        vm.SelectPageCommand.Execute("3"); // 设备管理 → 被拦
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        vm.SelectPageCommand.Execute("7"); // 设置 → 被拦
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);

        vm.SelectPageCommand.Execute("1"); // 产线（白名单内）→ 放行
        Assert.Equal(NavigationPageCatalog.ProductionLine.Index, vm.SelectedIndex);

        _appSettings.RunMode = KanbanRunMode.Full; // 还原，避免影响同集合其他用例
    }

    [Fact]
    public void SwitchUserCommand_SuccessfulDowngrade_RefreshesIdentityAndReturnsHome()
    {
        var session = new UserSession();
        session.Login(new User { Username = "admin", DisplayName = "管理员", Role = UserRole.Admin });
        var loginDialog = new FakeLoginDialogService(() =>
        {
            session.Login(new User { Username = "operator", DisplayName = "操作员", Role = UserRole.Operator });
            return true;
        });
        var vm = NewVm(session, loginDialog);
        vm.SelectedIndex = NavigationPageCatalog.Settings.Index;

        vm.SwitchUserCommand.Execute(null);

        Assert.Equal("操作员", vm.CurrentUserDisplay);
        Assert.Equal(MainAPP.Resources.Strings.M332, vm.CurrentRoleText);
        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        Assert.DoesNotContain(vm.NavItems, item => item.Index == NavigationPageCatalog.Settings.Index);
    }

    [Fact]
    public void SwitchUserCommand_Cancelled_PreservesCurrentUserAndPage()
    {
        var session = new UserSession();
        session.Login(new User { Username = "admin", DisplayName = "管理员", Role = UserRole.Admin });
        var vm = NewVm(session, new FakeLoginDialogService(() => false));
        vm.SelectedIndex = NavigationPageCatalog.Settings.Index;

        vm.SwitchUserCommand.Execute(null);

        Assert.Equal("admin", session.CurrentUser?.Username);
        Assert.Equal(NavigationPageCatalog.Settings.Index, vm.SelectedIndex);
    }

    private sealed class FakeLoginDialogService : ILoginDialogService
    {
        private readonly Func<bool> _showDialog;

        public FakeLoginDialogService(Func<bool> showDialog)
        {
            _showDialog = showDialog;
        }

        public bool ShowDialog() => _showDialog();
    }

    // ───────────── 跨页跳转事件 ─────────────

    [Fact]
    public void ProductionLineViewModel_FocusDeviceRequested_SetsSelectedIndexToDeviceDetail()
    {
        var vm = NewVm();
        vm.SelectedIndex = 1; // 模拟当前停留在产线页

        // 产线页点击设备卡片请求：通过 FocusDeviceCommand 触发 FocusDeviceRequested 事件，
        // MainWindowViewModel 订阅后跳转到设备详情页（SelectedIndex=9）
        vm.ProductionLineViewModel.FocusDeviceCommand.Execute("any-device-id");

        Assert.Equal(NavigationPageCatalog.DeviceDetail.Index, vm.SelectedIndex);
    }

    [Fact]
    public void OverviewViewModel_FocusDeviceRequested_SetsSelectedIndexToDeviceDetail()
    {
        var vm = NewVm();
        vm.SelectedIndex = 5; // 模拟当前停留在概览页（索引 5 = 生产复盘）

        // 概览页点击设备行请求：通过 FocusDeviceCommand 触发 FocusDeviceRequested 事件，
        // MainWindowViewModel 订阅后跳转到设备详情页（SelectedIndex=9）
        vm.OverviewViewModel.FocusDeviceCommand.Execute("any-device-id");

        Assert.Equal(NavigationPageCatalog.DeviceDetail.Index, vm.SelectedIndex);
    }

    [Fact]
    public void HomeViewModel_ViewDeviceDetailRequested_SetsSelectedIndexNine()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.SelectedIndex); // 前置：默认在主页

        vm.HomeViewModel.SelectedDeviceId = "any-device-id";
        Assert.True(vm.HomeViewModel.ViewDeviceDetailCommand.CanExecute(null));
        vm.HomeViewModel.ViewDeviceDetailCommand.Execute(null);

        Assert.Equal(NavigationPageCatalog.DeviceDetail.Index, vm.SelectedIndex);
    }

    [Fact]
    public void HomeViewModel_ViewWorkOrderManagerRequested_NavigatesToWorkOrders()
    {
        var vm = NewVm();
        vm.SelectedIndex = NavigationPageCatalog.Home.Index;

        vm.HomeViewModel.ViewWorkOrderManagerCommand.Execute(null);

        Assert.Equal(NavigationPageCatalog.WorkOrder.Index, vm.SelectedIndex);
    }

    [Fact]
    public void DeviceDetailViewModel_GoBackRequested_NavigatesHome()
    {
        var vm = NewVm();
        _ = vm.DeviceDetailViewModel; // 强制懒加载并完成事件订阅
        vm.SelectedIndex = NavigationPageCatalog.DeviceDetail.Index;

        vm.DeviceDetailViewModel.GoBackCommand.Execute(null);

        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
    }

    [Fact]
    public void AlarmCenterViewModel_ViewAlarmHistoryRequested_NavigatesToHistory()
    {
        var device = new Device { Id = "device-1", Name = "设备一" };
        _deviceRepo.Devices.Add(device);
        var vm = NewVm();
        var alarm = new ActiveAlarmInfo(DateTime.Now, device.Id, device.Name, "高温报警", AlarmLevel.High, AlarmKind.Plc);

        vm.AlarmCenterViewModel.ViewAlarmHistoryCommand.Execute(alarm);

        Assert.Equal(NavigationPageCatalog.HistoryQuery.Index, vm.SelectedIndex);
        Assert.NotNull(vm.HistoryQueryViewModel);
    }

    [Fact]
    public void LazyLoadedPageEvents_AreUnsubscribedOnDispose()
    {
        var vm = NewVm();
        _ = vm.HomeViewModel;
        _ = vm.ProductionLineViewModel;
        _ = vm.DeviceDetailViewModel;
        _ = vm.AlarmCenterViewModel;
        vm.SelectedIndex = NavigationPageCatalog.Home.Index;
        vm.Dispose();

        vm.HomeViewModel.ViewWorkOrderManagerCommand.Execute(null);
        vm.DeviceDetailViewModel.GoBackCommand.Execute(null);
        vm.ProductionLineViewModel.FocusDeviceCommand.Execute("device-1");
        vm.AlarmCenterViewModel.ViewAlarmHistoryCommand.Execute(null);

        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        vm.Dispose();
    }

    // ───────────── 未保存离开保护：跨页导航拦截（集成：真实 VM 图 + FakeDialogService） ─────────────

    [Fact]
    public void Navigate_LeavingDeviceManager_DirtyAndNo_StaysOnPage()
    {
        var vm = NewVm();
        vm.Navigate(NavigationPageCatalog.DeviceManager.Key);
        Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);

        // 真实设备管理 VM：加设备并改名置脏
        vm.DeviceManagerViewModel.AddDeviceCommand.Execute(null);
        vm.DeviceManagerViewModel.SelectedDevice!.Name = "临时改名";
        Assert.True(vm.DeviceManagerViewModel.IsDirty);

        _dialog.ShowResult = MessageBoxResult.No;
        vm.Navigate(NavigationPageCatalog.Home.Key);

        // 拒绝离开：仍停留在设备管理页，且确实弹过 K734 确认
        Assert.Equal(NavigationPageCatalog.DeviceManager.Index, vm.SelectedIndex);
        Assert.Contains(_dialog.ShowCalls, c => c.Message.Contains("离开设备管理页", StringComparison.Ordinal));
    }

    [Fact]
    public void Navigate_LeavingDeviceManager_DirtyAndYes_MovesAway()
    {
        var vm = NewVm();
        vm.Navigate(NavigationPageCatalog.DeviceManager.Key);
        vm.DeviceManagerViewModel.AddDeviceCommand.Execute(null);
        vm.DeviceManagerViewModel.SelectedDevice!.Name = "临时改名";
        Assert.True(vm.DeviceManagerViewModel.IsDirty);

        _dialog.ShowResult = MessageBoxResult.Yes;
        vm.Navigate(NavigationPageCatalog.Home.Key);

        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
    }

    [Fact]
    public void Navigate_LeavingDeviceManager_Clean_MovesWithoutPrompt()
    {
        var vm = NewVm();
        vm.Navigate(NavigationPageCatalog.DeviceManager.Key);

        _dialog.ShowCalls.Clear();
        vm.Navigate(NavigationPageCatalog.Home.Key);

        Assert.Equal(NavigationPageCatalog.Home.Index, vm.SelectedIndex);
        Assert.DoesNotContain(_dialog.ShowCalls, c => c.Message.Contains("离开设备管理页", StringComparison.Ordinal));
    }

    [Fact]
    public void Navigate_ToDeviceManager_FromDeviceManager_NoPrompt()
    {
        var vm = NewVm();
        vm.Navigate(NavigationPageCatalog.DeviceManager.Key);
        vm.DeviceManagerViewModel.AddDeviceCommand.Execute(null);
        vm.DeviceManagerViewModel.SelectedDevice!.Name = "临时改名";
        Assert.True(vm.DeviceManagerViewModel.IsDirty);
        _dialog.ShowCalls.Clear();

        // 目标页即当前页（原地重进）不触发拦截，避免无意义弹窗
        vm.Navigate(NavigationPageCatalog.DeviceManager.Key);
        Assert.Empty(_dialog.ShowCalls);
    }
}
