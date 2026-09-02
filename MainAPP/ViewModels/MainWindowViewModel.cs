using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyControl.Controls;
using HandyControl.Data;
using LicenseManager.Models;
using LicenseManager.Services;
using Kanban.Client;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using MainAPP.Resources;

namespace MainAPP.ViewModels;

/// <summary>
/// 主窗口视图模型。
/// 实现 IDisposable：构造时订阅了 PlcConnectionManager 及多个子 ViewModel 的事件，
/// 释放时统一取消订阅，避免事件泄漏（订阅方长生命周期、发布方短生命周期时会导致发布方无法 GC）。
/// 子 ViewModel 自身的释放由 DI 容器负责，此处只解绑事件。
/// </summary>
public partial class MainWindowViewModel : ObservableObject, INavigationService, IDisposable
{
    // 注意：设备详情页是上下文页面（依赖选中设备），不作为侧边栏常驻导航项。
    // 入口在主页"查看详情"按钮（HomeViewModel.ViewDeviceDetailCommand），通过 SelectedIndex=9 切换。
    // 侧边栏视觉位置与页面 Index 存在错位（DeviceDetail=9 隐藏占位，UserManager=10/Audit=11 视觉位置为 9/10）：
    // ListBox 必须绑 SelectedItem（SelectedNavItem，含真实 Index）而非 SelectedIndex（视觉位置），
    // 由 VM 完成"视觉选择 → 页面索引"映射；程序导航到隐藏页时 SelectedNavItem=null（侧边栏无高亮）。
    // 导航名称直接从 s_navItems.AccessibleName 派生（见 GetNavName），避免维护第二份名称数组导致文案分叉。

    // 切换计时：记录从 SelectedIndex 变更到下次渲染完成的时间，用于监控导航切换耗时。
    private long _navSwitchStartTicks;
    private int _navFromIndex = -1;
    /// <summary>
    /// 应用全局设置
    /// </summary>
    public AppSettings AppSettings { get; }

    /// <summary>
    /// 设备管理视图模型（懒加载：页面首次进入时创建，见构造函数 Lazy 初始化注释）
    /// </summary>
    public DeviceManagerViewModel DeviceManagerViewModel => _deviceManagerLazy.Value;

    public HistoryQueryViewModel HistoryQueryViewModel => _historyQueryLazy.Value;

    public HomeViewModel HomeViewModel => _homeLazy.Value;

    public ProductionLineViewModel ProductionLineViewModel => _productionLineLazy.Value;

    public AlarmCenterViewModel AlarmCenterViewModel => _alarmCenterLazy.Value;

    public OverviewViewModel OverviewViewModel => _overviewLazy.Value;

    public DeviceDetailViewModel DeviceDetailViewModel => _deviceDetailLazy.Value;

    public WorkOrderManagerViewModel WorkOrderManagerViewModel => _workOrderManagerLazy.Value;

    public SettingsViewModel SettingsViewModel => _settingsLazy.Value;

    public RuntimeMonitoringViewModel? RuntimeMonitoringViewModel => _runtimeMonitoringLazy.Value;

    /// <summary>
    /// PLC 连接管理器（暴露给 UI 绑定连接状态/状态文本）
    /// </summary>
    public IPlcConnectionManager ConnectionManager { get; }

    /// <summary>PLC 是否未连接，用于全局连接状态横幅 Visibility 绑定。</summary>
    public bool IsPlcDisconnected => !ConnectionManager.IsConnected;

    /// <summary>PLC 是否正在尝试连接，用于区分连接中与已断开。</summary>
    public bool IsPlcConnecting => IsPlcDisconnected
        && ConnectionManager.ConnectionStatus.StartsWith(PlcConnectionManager.ConnectingStatusPrefix, StringComparison.Ordinal);

    /// <summary>当前是否为 Remote 模式（横幅文案区分 PLC 与采集服务）。</summary>
    public bool IsRemoteDataMode => AppSettings.DataMode == KanbanDataMode.Remote;

    /// <summary>
    /// 数据停滞判定阈值：超过该秒数未收到任何实时数据（快照/事件）视为采集停滞。
    /// Remote 模式快照 500ms 一帧，Local 模式 200ms 轮询，10s 无数据可断定链路卡死。
    /// </summary>
    private const int DataStaleThresholdSeconds = 10;

    /// <summary>最近一次收到实时数据的时间（Remote=Collector 快照/事件；Local=PLC 成功轮询）。</summary>
    private DateTime LastDataTimestamp => IsRemoteDataMode
        ? (_dataClient?.LastDataReceivedAt ?? default)
        : (_acquisitionService?.GetDiagnosticsSnapshot().LastSuccessfulAt ?? default);

    /// <summary>采集链路是否活跃（连接正常或采集运行中）。链路不活跃时不算"停滞"（横幅另有断连提示）。</summary>
    private bool IsAcquisitionActive => IsRemoteDataMode
        ? ConnectionManager.IsConnected
        : (_acquisitionService?.IsRunning ?? false);

    /// <summary>数据是否停滞：链路活跃但超过阈值未收到数据。</summary>
    public bool IsDataStale
    {
        get
        {
            var last = LastDataTimestamp;
            return IsAcquisitionActive
                && last != default
                && (DateTime.Now - last).TotalSeconds > DataStaleThresholdSeconds;
        }
    }

    /// <summary>数据停滞秒数（文案用）。</summary>
    public int DataStaleSeconds
    {
        get
        {
            var last = LastDataTimestamp;
            return last == default ? 0 : (int)(DateTime.Now - last).TotalSeconds;
        }
    }

    /// <summary>全局横幅可见性：连接断开或数据停滞（连接正常但采集卡死）。</summary>
    public bool IsConnectionBannerVisible => IsPlcDisconnected || IsDataStale;

    /// <summary>全局连接状态横幅文案（连接断开 / 连接中 / 数据停滞三种态，Remote 文案指向采集服务）。</summary>
    public string PlcConnectionBannerText
    {
        get
        {
            if (IsDataStale && !IsPlcDisconnected)
                return IsRemoteDataMode
                    ? string.Format(Strings.F132, DataStaleSeconds)
                    : string.Format(Strings.F131, DataStaleSeconds);
            return IsPlcConnecting
                ? IsRemoteDataMode
                    ? string.Format(Strings.F159, ConnectionManager.ConnectionStatus)
                    : string.Format(Strings.F015, ConnectionManager.ConnectionStatus)
                : IsRemoteDataMode
                    ? string.Format(Strings.F232, ConnectionManager.ConnectionStatus)
                    : string.Format(Strings.F010, ConnectionManager.ConnectionStatus);
        }
    }

    /// <summary>
    /// 授权门禁（暴露给 UI 绑定授权状态/剩余天数/机器码）
    /// </summary>
    public LicenseGate LicenseGate { get; }

    /// <summary>授权状态枚举（UI 用 DataTrigger 切换颜色）</summary>
    public LicenseStatus LicenseStatus => LicenseGate.CurrentStatus;

    /// <summary>试用剩余天数（已激活或未启动试用时为 null）</summary>
    public int? RemainingTrialDays => LicenseGate.RemainingTrialDays;

    /// <summary>授权状态文本（侧边栏底部显示）</summary>
    public string LicenseStatusText
    {
        get
        {
            var status = LicenseGate.CurrentStatus;
            return status switch
            {
                LicenseStatus.Active when LicenseGate.CurrentLicense?.IsPermanent == false
                    => string.Format(Strings.F112, LicenseGate.CurrentLicense.ExpireDate),
                LicenseStatus.Active => Strings.M135,
                LicenseStatus.Trial => string.Format(Strings.F211, RemainingTrialDays ?? 0),
                LicenseStatus.TrialExpired => Strings.M136,
                LicenseStatus.TrialManipulated => Strings.M137,
                LicenseStatus.Expired => Strings.M138,
                LicenseStatus.MachineMismatch => Strings.M139,
                _ => Strings.M140,
            };
        }
    }

    /// <summary>授权状态详细提示（鼠标悬停时显示）</summary>
    public string LicenseStatusTooltip
    {
        get
        {
            var status = LicenseGate.CurrentStatus;
            var machineCode = LicenseGate.MachineCode;
            return status switch
            {
                LicenseStatus.Active when LicenseGate.CurrentLicense?.IsPermanent == false
                    => string.Format(Strings.F150, machineCode, LicenseGate.CurrentLicense!.ProductKey, LicenseGate.CurrentLicense.ExpireDate),
                LicenseStatus.Active => string.Format(Strings.F149, machineCode),
                LicenseStatus.Trial => string.Format(Strings.F151, machineCode, RemainingTrialDays ?? 0),
                LicenseStatus.TrialExpired => string.Format(Strings.F152, machineCode),
                LicenseStatus.TrialManipulated => string.Format(Strings.F148, machineCode),
                LicenseStatus.Expired => string.Format(Strings.F147, machineCode),
                LicenseStatus.MachineMismatch => string.Format(Strings.F146, machineCode),
                _ => string.Format(Strings.F145, machineCode),
            };
        }
    }

    /// <summary>
    /// 侧边栏导航项集合（绑定到 ListBox.ItemsSource）。
    /// 替代原 hc:SideMenu 的 SideMenuItem 子元素声明方式。
    /// AccessibleName 通过 ItemContainerStyle 绑定到 ListBoxItem.AutomationProperties.Name，
    /// 使 UIA 客户端（FlaUI/WinAppDriver）可通过 ByName 直接定位每个导航项并 Click。
    /// 登录/退出登录后通过 RefreshNavigationForCurrentUser 重建集合以反映角色权限变化。
    /// </summary>
    private readonly ObservableCollection<NavItem> _navItemsBacking = new();
    public ReadOnlyObservableCollection<NavItem> NavItems { get; }

    /// <summary>当前登录用户会话（驱动导航权限过滤）。</summary>
    public UserSession UserSession { get; }

    private readonly ILoginDialogService? _loginDialogService;
    private readonly IUserHelpService? _userHelpService;

    /// <summary>当前用户显示名，供侧边栏用户卡片绑定。</summary>
    public string CurrentUserDisplay => UserSession.CurrentUserDisplay;

    /// <summary>当前用户角色的本地化名称。</summary>
    public string CurrentRoleText => UserSession.CurrentRole switch
    {
        UserRole.Admin => Strings.M334,
        UserRole.Engineer => Strings.M333,
        _ => Strings.M332,
    };

    /// <summary>
    /// 当前页面索引，由 Navigate()/侧边栏选择驱动；具体页面对应 NavigationPageCatalog 命名键（如 "Home"、"DeviceDetail"）。
    /// 注意：这是"页面索引"（NavigationPageCatalog.Index），与侧边栏视觉位置不同——隐藏页（DeviceDetail=9）
    /// 不显示在 NavItems 中，侧边栏点击必须经 <see cref="SelectedNavItem"/>（含 Index）映射后再写本属性，
    /// 禁止把 ListBox.SelectedIndex（视觉位置）直接双向绑到本属性，否则视觉位置 ≥9 的项会错位。
    /// </summary>
    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>
    /// 侧边栏当前选中的导航项（ListBox.SelectedItem 双向绑定）。
    /// 侧边栏视觉位置与页面 Index 存在错位（DeviceDetail=9 隐藏占位），故以 NavItem.Index 为
    /// 中介完成"视觉选择 → 页面索引"的映射；程序导航到隐藏页时本属性为 null（侧边栏无高亮）。
    /// </summary>
    [ObservableProperty]
    private NavItem? _selectedNavItem;

    /// <summary>侧边栏选中项变化：把 NavItem.Index 映射为页面索引（仅当确实不同，防与
    /// OnSelectedIndexChanged 反向同步形成循环）。程序导航到隐藏页时 value 为 null，不做任何事。</summary>
    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is not null && value.Index != SelectedIndex)
        {
            SelectedIndex = value.Index;
        }
    }

    /// <summary>
    /// 侧边栏是否折叠（true=窄栏仅图标，false=展开显示文字）
    /// 默认 false：配置员首次进入即可看到页面名称，确认位置后可手动折叠
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    // UI Dispatcher：构造时捕获（DI 在 UI 线程构造），用于切换后测量渲染完成耗时
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // 数据新鲜度监控：Remote 取 KanbanDataClient 最后收数时间，Local 取采集服务最后成功轮询
    private readonly KanbanDataClient? _dataClient;
    private readonly IPlcDataAcquisitionService? _acquisitionService;
    private readonly DispatcherTimer _staleCheckTimer;

    private void OnStaleCheckTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDataStale));
        OnPropertyChanged(nameof(DataStaleSeconds));
        OnPropertyChanged(nameof(IsConnectionBannerVisible));
        OnPropertyChanged(nameof(PlcConnectionBannerText));
    }

    /// <summary>看板标题配置变更 → 刷新窗口标题（命名方法，Dispose 时解绑）。</summary>
    private void OnAppSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AppTitle))
            OnPropertyChanged(nameof(WindowTitle));
    }

    private static IReadOnlyList<NavigationPageDefinition> CatalogDefinitions => NavigationPageCatalog.All;

    /// <summary>
    /// 按索引获取导航项名称（用于日志），取 AccessibleName（完整名称如"设备管理"）。
    /// 索引越界时返回 fallback，避免日志抛异常。
    /// </summary>
    private static string GetNavName(int index, string fallback) =>
        CatalogDefinitions.FirstOrDefault(page => page.Index == index)?.NavItem.AccessibleName ?? fallback;

    public IReadOnlyList<NavigationPageDefinition> PageDefinitions => NavigationPageCatalog.All;

    /// <summary>
    /// Viewer（展示）模式下允许访问的页面键：主页/产线/报警中心 + 主页"查看详情"入口（设备详情）。
    /// 其余管理页（设备/工单/历史/复盘/设置/运行监控）在 Viewer 模式下不可达。
    /// </summary>
    private static readonly HashSet<string> ViewerAllowedPageKeys =
        ["Home", "ProductionLine", "AlarmCenter", "DeviceDetail", "DataSourceMonitoring"];

    /// <summary>是否 Viewer（展示）模式：侧栏只留展示页，导航受限，退出需确认。</summary>
    public bool IsViewerMode => AppSettings.RunMode == KanbanRunMode.Viewer;

    /// <summary>窗口标题（跟随 AppSettings.AppTitle，设置页保存后实时刷新）。</summary>
    public string WindowTitle => AppSettings.AppTitle;

    /// <summary>
    /// 基于名称导航到指定页面（INavigationService 实现）。
    /// Viewer 模式下仅允许展示页（<see cref="ViewerAllowedPageKeys"/>），管理页跳转被忽略并记日志。
    /// 角色权限不足时拒绝导航并记日志（如 Operator 导航到 DeviceManager）。
    /// </summary>
    public void Navigate(string pageKey)
    {
        if (IsViewerMode && !ViewerAllowedPageKeys.Contains(pageKey))
        {
            Log.Warning("Viewer 模式 拒绝导航到管理页 {PageKey}", pageKey);
            return;
        }
        var page = PageDefinitions.FirstOrDefault(item => item.Key == pageKey);
        if (page is not null)
        {
            // 角色权限检查：页面有 RequiredRole 时，当前用户角色必须满足
            if (page.RequiredRole is { } required && !UserSession.CurrentRole.AtLeast(required))
            {
                Log.Warning("角色 {Role} 无权访问页面 {PageKey}（需 {Required}）",
                    UserSession.CurrentRole, pageKey, required);
                return;
            }
            // 未保存离开保护（检查 2026-08-30）：离开设备管理页且存在未保存更改时需二次确认。
            // 短路求值保证仅在当前正处于设备管理页时才触达 DeviceManagerViewModel（其必然已实例化）。
            // 登录/权限变更触发的强制回退（RefreshNavigationForCurrentUser 直改 SelectedIndex）不经过此处，属有意豁免。
            if (SelectedIndex == NavigationPageCatalog.DeviceManager.Index
                && page.Index != NavigationPageCatalog.DeviceManager.Index
                && !_deviceManagerLazy.Value.MayDiscardUnsavedAndLeave())
            {
                Log.Debug("离开设备管理页被未保存确认拦截");
                return;
            }
            SelectedIndex = page.Index;
        }
        else
        {
            Log.Warning("导航 未知页面键 {PageKey}，已忽略", pageKey);
        }
    }

    /// <summary>
    /// 重建侧边栏导航项：按 Viewer 模式 + 当前用户角色过滤。
    /// 构造时和登录/退出登录后调用。
    /// </summary>
    private void RebuildSidebarItems()
    {
        _navItemsBacking.Clear();
        foreach (var page in PageDefinitions.Where(p => p.ShowInSidebar
            && (!IsViewerMode || ViewerAllowedPageKeys.Contains(p.Key))
            && (p.RequiredRole is null || UserSession.CurrentRole.AtLeast(p.RequiredRole.Value))))
        {
            _navItemsBacking.Add(page.NavItem);
        }
        // 列表重建后重新对齐侧边栏高亮：SelectedIndex 若未变化不会触发 OnSelectedIndexChanged，
        // 必须在此显式同步（构造期/登录后 SelectedIndex 保持原值时保证高亮正确）。
        SelectedNavItem = _navItemsBacking.FirstOrDefault(n => n.Index == SelectedIndex);
    }

    /// <summary>
    /// 登录/退出登录后刷新导航：重建侧边栏并跳转到合法页面。
    /// 若当前页在权限变更后不可访问（如退出登录后停留在设备管理页），回退到主页。
    /// </summary>
    public void RefreshNavigationForCurrentUser()
    {
        RebuildSidebarItems();
        // 检查当前页是否仍可访问：SelectedIndex 对应的页面需通过角色检查
        var currentPage = PageDefinitions.FirstOrDefault(p => p.Index == SelectedIndex);
        if (currentPage?.RequiredRole is { } required && !UserSession.CurrentRole.AtLeast(required))
        {
            SelectedIndex = 0; // 回退到主页
        }
    }

    /// <summary>
    /// 运行中切换用户。Viewer 纯展示模式不开放登录入口；
    /// 取消或验证失败时保留原会话，成功后刷新用户显示及角色导航。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSwitchUser))]
    private void SwitchUser()
    {
        if (_loginDialogService?.ShowDialog() != true)
            return;

        OnPropertyChanged(nameof(CurrentUserDisplay));
        OnPropertyChanged(nameof(CurrentRoleText));
        RefreshNavigationForCurrentUser();
        Log.Information("运行中用户切换成功：{Username} ({Role})",
            UserSession.CurrentUser?.Username, UserSession.CurrentRole);
    }

    private bool CanSwitchUser() => !IsViewerMode && _loginDialogService is not null;

    public MainWindowViewModel(
        AppSettings appSettings,
        IServiceProvider serviceProvider,
        IPlcConnectionManager connectionManager,
        LicenseGate licenseGate,
        UserSession userSession,
        KanbanDataClient? dataClient = null,
        IPlcDataAcquisitionService? acquisitionService = null,
        ILoginDialogService? loginDialogService = null,
        IUserHelpService? userHelpService = null)
    {
        AppSettings = appSettings;
        _serviceProvider = serviceProvider;
        ConnectionManager = connectionManager;
        LicenseGate = licenseGate;
        UserSession = userSession;
        _loginDialogService = loginDialogService;
        _userHelpService = userHelpService;
        _dataClient = dataClient;
        _acquisitionService = acquisitionService;

        // 页面 ViewModel 懒加载（2026-08-11 启动优化）：
        // 各页面 VM 只在首次进入页面时才创建（NavigationPage.ViewModel 是 Lazy，触发点
        // NavigationPageHost 可见 / ActivatePage 导航），构造期不再全量实例化 10+ VM，
        // 消除 MainWindow 实例化阶段的重型服务链（HistoryService/EF Core/Settings 服务树等）JIT 成本。
        // 首页例外：MainWindow 构造时 ActivatePage(0) 立即创建（首屏必需，语义不变）。
        // 跨页事件订阅在 VM 首次创建时挂接（SubscribeXxx），事件只会在对应页面被用户操作时
        // 触发——此时 VM 必然已创建，订阅时序无竞态。
        _deviceManagerLazy = CreateLazy(GetService<DeviceManagerViewModel>, SubscribeDeviceManager);
        _historyQueryLazy = CreateLazy(GetService<HistoryQueryViewModel>);
        _homeLazy = CreateLazy(GetService<HomeViewModel>, SubscribeHome);
        _productionLineLazy = CreateLazy(GetService<ProductionLineViewModel>, SubscribeProductionLine);
        _alarmCenterLazy = CreateLazy(GetService<AlarmCenterViewModel>, SubscribeAlarmCenter);
        _overviewLazy = CreateLazy(GetService<OverviewViewModel>, SubscribeOverview);
        _deviceDetailLazy = CreateLazy(GetService<DeviceDetailViewModel>, SubscribeDeviceDetail);
        _workOrderManagerLazy = CreateLazy(GetService<WorkOrderManagerViewModel>);
        _settingsLazy = CreateLazy(GetService<SettingsViewModel>);
        _runtimeMonitoringLazy = CreateLazy(GetService<RuntimeMonitoringViewModel>);

        // 数据新鲜度检查：2s 轮询刷新"数据停滞"横幅（连接正常但采集卡死时提示）
        _staleCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _staleCheckTimer.Tick += OnStaleCheckTick;
        _staleCheckTimer.Start();
        // 窗口标题跟随看板标题配置（设置页保存后实时生效；AppSettings 为进程级单例，生命周期与本 VM 一致）。
        // 命名方法订阅（遵守本文件"事件全部用命名方法"约定），Dispose 精确解绑。
        AppSettings.PropertyChanged += OnAppSettingsPropertyChanged;
        NavItems = new ReadOnlyObservableCollection<NavItem>(_navItemsBacking);
        RebuildSidebarItems();
        ConnectionManager.ConnectionStateChanged += OnConnectionStateChanged;
        // 横幅依赖 IsConnected 与 ConnectionStatus，连接尝试期间两者都会变化。
        ConnectionManager.PropertyChanged += OnConnectionManagerPropertyChanged;
    }

    /// <summary>页面 ViewModel 惰性包装：工厂 + 首次创建时的事件订阅回调。</summary>
    private static Lazy<T> CreateLazy<T>(Func<T> factory, Action<T>? onCreated = null)
        where T : class
        => new(() =>
        {
            var instance = factory();
            onCreated?.Invoke(instance);
            return instance;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>懒加载服务解析入口（DI 容器，页面 VM 首次进入时解析）。</summary>
    private T GetService<T>() where T : class
        => _serviceProvider.GetRequiredService<T>();

    private readonly IServiceProvider _serviceProvider;

    // ──────────── 页面 VM 惰性字段与订阅挂接（Dispose 按 IsValueCreated 解绑）────────────

    private readonly Lazy<DeviceManagerViewModel> _deviceManagerLazy;
    private readonly Lazy<HistoryQueryViewModel> _historyQueryLazy;
    private readonly Lazy<HomeViewModel> _homeLazy;
    private readonly Lazy<ProductionLineViewModel> _productionLineLazy;
    private readonly Lazy<AlarmCenterViewModel> _alarmCenterLazy;
    private readonly Lazy<OverviewViewModel> _overviewLazy;
    private readonly Lazy<DeviceDetailViewModel> _deviceDetailLazy;
    private readonly Lazy<WorkOrderManagerViewModel> _workOrderManagerLazy;
    private readonly Lazy<SettingsViewModel> _settingsLazy;
    private readonly Lazy<RuntimeMonitoringViewModel> _runtimeMonitoringLazy;
    private readonly HashSet<object> _attachedPageViewModels = new();

    /// <summary>
    /// 页面模块直接从 DI 创建 ViewModel 时的订阅入口。
    /// 页面模块与本 VM 共用 DI 单例，但不会经过本 VM 的 Lazy 包装，
    /// 因此必须在页面真正激活时补挂跨页事件。
    /// </summary>
    internal void AttachPageViewModel(object pageViewModel)
    {
        switch (pageViewModel)
        {
            case HomeViewModel home:
                SubscribeHome(home);
                break;
            case ProductionLineViewModel productionLine:
                SubscribeProductionLine(productionLine);
                break;
            case OverviewViewModel overview:
                SubscribeOverview(overview);
                break;
            case DeviceDetailViewModel deviceDetail:
                SubscribeDeviceDetail(deviceDetail);
                break;
            case AlarmCenterViewModel alarmCenter:
                SubscribeAlarmCenter(alarmCenter);
                break;
        }
    }

    /// <summary>产线页"跳转主页"请求：通过名称导航到主页</summary>
    private void SubscribeProductionLine(ProductionLineViewModel vm)
    {
        if (_attachedPageViewModels.Add(vm))
            vm.FocusDeviceRequested += OnFocusDeviceRequested;
    }

    /// <summary>概览页"跳转主页"请求：通过名称导航到主页</summary>
    private void SubscribeOverview(OverviewViewModel vm)
    {
        if (_attachedPageViewModels.Add(vm))
            vm.FocusDeviceRequested += OnFocusDeviceRequested;
    }

    /// <summary>设备详情页"返回主页"请求：通过名称导航到主页。</summary>
    private void SubscribeDeviceDetail(DeviceDetailViewModel vm)
    {
        if (!_attachedPageViewModels.Add(vm)) return;
        vm.GoBackRequested += OnGoBackRequested;
        vm.ViewAlarmHistoryRequested += OnViewAlarmHistoryRequested;
    }

    /// <summary>
    /// 主页：设备详情跳转（HomeViewModel 已将设备 Id 写入 IDeviceSelectionService，
    /// DeviceDetailViewModel 自动响应）与工单管理跳转。
    /// </summary>
    private void SubscribeHome(HomeViewModel vm)
    {
        if (!_attachedPageViewModels.Add(vm)) return;
        vm.ViewDeviceDetailRequested += OnViewDeviceDetailRequested;
        vm.ViewWorkOrderManagerRequested += OnViewWorkOrderManagerRequested;
    }

    private void SubscribeAlarmCenter(AlarmCenterViewModel vm)
    {
        if (_attachedPageViewModels.Add(vm))
            vm.ViewAlarmHistoryRequested += OnViewAlarmHistoryRequested;
    }

    private void SubscribeDeviceManager(DeviceManagerViewModel vm) { }

    /// <summary>
    /// 释放事件订阅，避免事件泄漏。
    /// 多次调用安全：_disposed 守卫防止重复取消订阅触发空引用或逻辑异常。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _staleCheckTimer.Stop();
        AppSettings.PropertyChanged -= OnAppSettingsPropertyChanged;
        ConnectionManager.ConnectionStateChanged -= OnConnectionStateChanged;
        ConnectionManager.PropertyChanged -= OnConnectionManagerPropertyChanged;
        foreach (var pageViewModel in _attachedPageViewModels)
        {
            switch (pageViewModel)
            {
                case ProductionLineViewModel productionLine:
                    productionLine.FocusDeviceRequested -= OnFocusDeviceRequested;
                    break;
                case OverviewViewModel overview:
                    overview.FocusDeviceRequested -= OnFocusDeviceRequested;
                    break;
                case DeviceDetailViewModel deviceDetail:
                    deviceDetail.GoBackRequested -= OnGoBackRequested;
                    deviceDetail.ViewAlarmHistoryRequested -= OnViewAlarmHistoryRequested;
                    break;
                case HomeViewModel home:
                    home.ViewDeviceDetailRequested -= OnViewDeviceDetailRequested;
                    home.ViewWorkOrderManagerRequested -= OnViewWorkOrderManagerRequested;
                    break;
                case AlarmCenterViewModel alarmCenter:
                    alarmCenter.ViewAlarmHistoryRequested -= OnViewAlarmHistoryRequested;
                    break;
            }
        }
        _attachedPageViewModels.Clear();
    }

    private bool _disposed;

    // ──────────── 事件处理方法（构造时订阅，Dispose 时取消订阅）────────────
    // 全部使用命名方法而非 lambda，确保 Dispose 中可用 -= 精确取消订阅。

    /// <summary>
    /// 横幅依赖 IsConnected 与 ConnectionStatus，连接尝试期间两者都会变化。
    /// 后台线程触发时需封送到 UI 线程后再触发属性变更通知。
    /// </summary>
    private void OnConnectionManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlcConnectionManager.IsConnected) or nameof(PlcConnectionManager.ConnectionStatus))
        {
            void NotifyConnectionState()
            {
                OnPropertyChanged(nameof(IsPlcDisconnected));
                OnPropertyChanged(nameof(IsPlcConnecting));
                OnPropertyChanged(nameof(PlcConnectionBannerText));
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                _ = dispatcher.InvokeAsync(NotifyConnectionState);
            else
                NotifyConnectionState();
        }
    }

    /// <summary>
    /// 产线页/概览页点击设备卡片：直达设备详情页（而非主页），
    /// 避免用户在产线页点卡片却跳回主页仪表板。
    /// </summary>
    private void OnFocusDeviceRequested(string _) => Navigate("DeviceDetail");

    /// <summary>设备详情页"返回主页"请求。</summary>
    private void OnGoBackRequested() => Navigate("Home");

    /// <summary>主页"查看设备详情"请求：跳转到设备详情页。</summary>
    private void OnViewDeviceDetailRequested(string _) => Navigate("DeviceDetail");

    /// <summary>主页"工单管理"请求：跳转到工单管理页。</summary>
    private void OnViewWorkOrderManagerRequested() => Navigate("WorkOrder");

    /// <summary>设备详情页/报警中心"查看报警历史"请求：跳转到历史查询页。</summary>
    private void OnViewAlarmHistoryRequested(string deviceId, string alarmName)
    {
        HistoryQueryViewModel.PrepareAlarmHistory(deviceId, alarmName);
        Navigate("HistoryQuery");
    }

    /// <summary>
    /// 刷新授权状态属性通知（激活成功后调用，触发 UI 重新读取计算属性）。
    /// LicenseGate 不是 INotifyPropertyChanged，需手动触发属性变更通知。
    /// </summary>
    public void RefreshLicenseStatus()
    {
        OnPropertyChanged(nameof(LicenseStatus));
        OnPropertyChanged(nameof(RemainingTrialDays));
        OnPropertyChanged(nameof(LicenseStatusText));
        OnPropertyChanged(nameof(LicenseStatusTooltip));
    }

    /// <summary>PLC 连接状态边沿回调：在后台采集线程触发，必须用 UI 线程封送后弹 Growl。
    /// 事件仅 断开/重连 各触发一次，不会刷屏。Remote 模式下文案指向 Collector 采集服务。</summary>
    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
            if (IsRemoteDataMode)
            {
                if (!e.IsConnected)
                    Growl.Error(new GrowlInfo
                    {
                        Message = string.Format(Strings.F233, e.IpAddress, e.DisconnectCount),
                        ShowDateTime = false,
                    });
                else
                {
                    var dur = e.DisconnectDuration.HasValue
                        ? $"{e.DisconnectDuration.Value.TotalSeconds:F0}s"
                        : "—";
                    Growl.Success(new GrowlInfo
                    {
                        Message = string.Format(Strings.F234, dur),
                        ShowDateTime = false,
                    });
                }
                return;
            }

            if (!e.IsConnected)
            {
                Growl.Error(new GrowlInfo
                {
                    Message = string.Format(Strings.F011, e.IpAddress, e.DisconnectCount),
                    ShowDateTime = false,
                });
            }
            else
            {
                var dur = e.DisconnectDuration.HasValue
                    ? $"{e.DisconnectDuration.Value.TotalSeconds:F0}s"
                    : "—";
                Growl.Success(new GrowlInfo
                {
                    Message = string.Format(Strings.F012, dur),
                    ShowDateTime = false,
                });
            }
        });
    }

    /// <summary>
    /// SelectedIndex 变更回调：记录切换开始时刻，并在渲染完成后（Background 优先级）
    /// 记录耗时用于监控导航切换性能。预热流程也会经过此方法。
    /// </summary>
    partial void OnSelectedIndexChanged(int value)
    {
        _navSwitchStartTicks = Stopwatch.GetTimestamp();
        var fromName = GetNavName(_navFromIndex, "初始");
        var toName = GetNavName(value, value.ToString());
        Log.Debug("导航 切换 {From} → {To} 开始", fromName, toName);

        // 反向同步侧边栏高亮：仅当目标页是可见导航项时选中对应 NavItem；
        // 隐藏页（如 DeviceDetail=9）在 NavItems 中不存在 → 置 null 取消高亮。
        var visibleItem = _navItemsBacking.FirstOrDefault(n => n.Index == value);
        if (!ReferenceEquals(SelectedNavItem, visibleItem))
        {
            SelectedNavItem = visibleItem;
        }

        if (value == NavigationPageCatalog.DeviceDetail.Index)
        {
            _dispatcher.BeginInvoke(
                () => DeviceDetailViewModel.RefreshOnEnter(),
                DispatcherPriority.Background);
        }

        // Background 优先级在 Render（渲染）之后执行，近似"用户可见切换完成"时刻
        _dispatcher.BeginInvoke(new Action(() =>
        {
            var ms = Stopwatch.GetTimestamp() - _navSwitchStartTicks;
            var elapsedMs = ms * 1000.0 / Stopwatch.Frequency;
            Log.Debug("导航 切换 {From} → {To} 渲染完成，耗时 {ElapsedMs:F1}ms", fromName, toName, elapsedMs);
        }), DispatcherPriority.Background);
    }

    partial void OnSelectedIndexChanging(int value)
    {
        _navFromIndex = SelectedIndex;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary>打开内置使用手册（F1 / 帮助按钮）。</summary>
    [RelayCommand]
    private void OpenHelp()
    {
        var owner = Application.Current?.MainWindow;
        _userHelpService?.OpenUserManual(owner);
    }

    /// <summary>
    /// 跳转到设置页（授权状态卡片点击时调用）。
    /// </summary>
    [RelayCommand]
    private void GoToLicenseSettings() => Navigate("Settings");

    /// <summary>
    /// 全局快捷键 Ctrl+1~8 切换主导航页（主页/产线/报警中心/设备/历史/复盘/设置/工单）。
    /// 设备详情页是上下文页面，不在此快捷键范围内（入口在主页"查看详情"按钮）。
    /// </summary>
    [RelayCommand]
    private void SelectPage(string indexStr)
    {
        // 转调 Navigate 复用同一套权限门禁（Viewer 白名单 + RequiredRole），
        // 修复快捷键绕过角色/Viewer 限制直达管理页的漏洞（审查修复 2026-08-13）。
        if (int.TryParse(indexStr, out var idx))
        {
            var page = PageDefinitions.FirstOrDefault(p => p.ShowInSidebar && p.Index == idx);
            if (page is not null)
                Navigate(page.Key);
        }
    }
}
