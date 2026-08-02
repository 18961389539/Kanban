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
using MainAPP.Models;
using MainAPP.Services;
using Material.Icons;
using Serilog;

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
    // s_navItems 位置索引 0~8 对应侧边栏 9 项；SelectedIndex=9 保留给设备详情上下文页。
    // 导航名称直接从 s_navItems.AccessibleName 派生（见 GetNavName），避免维护第二份名称数组导致文案分叉。

    // 切换计时：记录从 SelectedIndex 变更到下次渲染完成的时间，用于排查切换卡顿
    private long _navSwitchStartTicks;
    private int _navFromIndex = -1;
    /// <summary>
    /// 应用全局设置
    /// </summary>
    public AppSettings AppSettings { get; }

    /// <summary>
    /// 设备管理视图模型
    /// </summary>
    public DeviceManagerViewModel DeviceManagerViewModel { get; }

    public HistoryQueryViewModel HistoryQueryViewModel { get; }

    public HomeViewModel HomeViewModel { get; }

    public ProductionLineViewModel ProductionLineViewModel { get; }

    public AlarmCenterViewModel AlarmCenterViewModel { get; }

    public OverviewViewModel OverviewViewModel { get; }

    public DeviceDetailViewModel DeviceDetailViewModel { get; }

    public WorkOrderManagerViewModel WorkOrderManagerViewModel { get; }

    public SettingsViewModel SettingsViewModel { get; }

    public RuntimeMonitoringViewModel? RuntimeMonitoringViewModel { get; }

    /// <summary>
    /// PLC 连接管理器（暴露给 UI 绑定连接状态/状态文本）
    /// </summary>
    public PlcConnectionManager ConnectionManager { get; }

    /// <summary>PLC 是否未连接，用于全局连接状态横幅 Visibility 绑定。</summary>
    public bool IsPlcDisconnected => !ConnectionManager.IsConnected;

    /// <summary>PLC 是否正在尝试连接，用于区分连接中与已断开。</summary>
    public bool IsPlcConnecting => IsPlcDisconnected && ConnectionManager.ConnectionStatus.StartsWith("正在连接", StringComparison.Ordinal);

    /// <summary>当前是否为 Remote 模式（横幅文案区分 PLC 与采集服务）。</summary>
    public bool IsRemoteDataMode => AppSettings.DataMode == KanbanDataMode.Remote;

    /// <summary>全局连接状态横幅文案（Remote 模式下文案指向 Collector 采集服务）。</summary>
    public string PlcConnectionBannerText => IsPlcConnecting
        ? IsRemoteDataMode
            ? $"正在连接采集服务 · {ConnectionManager.ConnectionStatus}"
            : $"PLC 正在连接 · {ConnectionManager.ConnectionStatus}"
        : IsRemoteDataMode
            ? $"采集服务已断开 · {ConnectionManager.ConnectionStatus}"
            : $"PLC 已断开 · {ConnectionManager.ConnectionStatus}";

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
                    => $"已激活 · {LicenseGate.CurrentLicense.ExpireDate:yyyy-MM-dd}",
                LicenseStatus.Active => "已激活",
                LicenseStatus.Trial => $"试用 · 剩 {RemainingTrialDays ?? 0} 天",
                LicenseStatus.TrialExpired => "试用已过期",
                LicenseStatus.TrialManipulated => "试用异常",
                LicenseStatus.Expired => "授权已过期",
                LicenseStatus.MachineMismatch => "授权不匹配",
                _ => "未激活",
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
                    => $"机器码：{machineCode}\n激活码：{LicenseGate.CurrentLicense!.ProductKey}\n到期：{LicenseGate.CurrentLicense.ExpireDate:yyyy-MM-dd}",
                LicenseStatus.Active => $"机器码：{machineCode}\n永久授权",
                LicenseStatus.Trial => $"机器码：{machineCode}\n试用期剩余 {RemainingTrialDays ?? 0} 天",
                LicenseStatus.TrialExpired => $"机器码：{machineCode}\n试用期已过期，请激活",
                LicenseStatus.TrialManipulated => $"机器码：{machineCode}\n检测到系统时间异常",
                LicenseStatus.Expired => $"机器码：{machineCode}\n授权已过期，请重新激活",
                LicenseStatus.MachineMismatch => $"机器码：{machineCode}\n授权与当前机器不匹配",
                _ => $"机器码：{machineCode}",
            };
        }
    }

    /// <summary>
    /// 侧边栏导航项集合（绑定到 ListBox.ItemsSource）。
    /// 替代原 hc:SideMenu 的 SideMenuItem 子元素声明方式。
    /// AccessibleName 通过 ItemContainerStyle 绑定到 ListBoxItem.AutomationProperties.Name，
    /// 使 UIA 客户端（FlaUI/WinAppDriver）可通过 ByName 直接定位每个导航项并 Click。
    /// </summary>
    public ReadOnlyObservableCollection<NavItem> NavItems { get; }

    /// <summary>
    /// 当前页面索引（0:主页 1:设备管理 2:历史查询 3:设置）
    /// </summary>
    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>
    /// 侧边栏是否折叠（true=窄栏仅图标，false=展开显示文字）
    /// 默认 false：配置员首次进入即可看到页面名称，确认位置后可手动折叠
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    // UI Dispatcher：构造时捕获（DI 在 UI 线程构造），用于切换后测量渲染完成耗时
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private static IReadOnlyList<NavigationPageDefinition> CatalogDefinitions => NavigationPageCatalog.All;

    /// <summary>
    /// 按索引获取导航项名称（用于日志），取 AccessibleName（完整名称如"设备管理"）。
    /// 索引越界时返回 fallback，避免日志抛异常。
    /// </summary>
    private static string GetNavName(int index, string fallback) =>
        CatalogDefinitions.FirstOrDefault(page => page.Index == index)?.NavItem.AccessibleName ?? fallback;

    public IReadOnlyList<NavigationPageDefinition> PageDefinitions => NavigationPageCatalog.All;

    /// <summary>
    /// 基于名称导航到指定页面（INavigationService 实现）。
    /// 通过 s_pageIndices 字典查找索引，避免与 s_navItems 顺序强耦合的魔术数字。
    /// 未知 pageKey 记日志后不跳转，防止静默失败。
    /// </summary>
    public void Navigate(string pageKey)
    {
        var page = PageDefinitions.FirstOrDefault(item => item.Key == pageKey);
        if (page is not null)
        {
            SelectedIndex = page.Index;
        }
        else
        {
            Log.Warning("导航 未知页面键 {PageKey}，已忽略", pageKey);
        }
    }

    public MainWindowViewModel(
        AppSettings appSettings,
        DeviceManagerViewModel deviceManagerViewModel,
        HistoryQueryViewModel historyQueryViewModel,
        HomeViewModel homeViewModel,
        ProductionLineViewModel productionLineViewModel,
        AlarmCenterViewModel alarmCenterViewModel,
        OverviewViewModel overviewViewModel,
        DeviceDetailViewModel deviceDetailViewModel,
        WorkOrderManagerViewModel workOrderManagerViewModel,
        SettingsViewModel settingsViewModel,
        PlcConnectionManager connectionManager,
        LicenseGate licenseGate,
        RuntimeMonitoringViewModel? runtimeMonitoringViewModel = null)
    {
        AppSettings = appSettings;
        DeviceManagerViewModel = deviceManagerViewModel;
        HistoryQueryViewModel = historyQueryViewModel;
        HomeViewModel = homeViewModel;
        ProductionLineViewModel = productionLineViewModel;
        AlarmCenterViewModel = alarmCenterViewModel;
        OverviewViewModel = overviewViewModel;
        DeviceDetailViewModel = deviceDetailViewModel;
        WorkOrderManagerViewModel = workOrderManagerViewModel;
        SettingsViewModel = settingsViewModel;
        RuntimeMonitoringViewModel = runtimeMonitoringViewModel;
        ConnectionManager = connectionManager;
        LicenseGate = licenseGate;
        var sidebarItems = new ObservableCollection<NavItem>(
            PageDefinitions.Where(page => page.ShowInSidebar).Select(page => page.NavItem));
        NavItems = new ReadOnlyObservableCollection<NavItem>(sidebarItems);
        ConnectionManager.ConnectionStateChanged += OnConnectionStateChanged;
        // 横幅依赖 IsConnected 与 ConnectionStatus，连接尝试期间两者都会变化。
        ConnectionManager.PropertyChanged += OnConnectionManagerPropertyChanged;
        // 产线页"跳转主页"请求：通过名称导航到主页
        ProductionLineViewModel.FocusDeviceRequested += OnFocusDeviceRequested;
        // 概览页"跳转主页"请求：通过名称导航到主页
        OverviewViewModel.FocusDeviceRequested += OnFocusDeviceRequested;
        // 设备详情页"返回主页"请求：通过名称导航到主页
        DeviceDetailViewModel.GoBackRequested += OnGoBackRequested;
        // 主页"查看设备详情"请求：跳转到设备详情页。
        // HomeViewModel 已将设备 Id 写入 IDeviceSelectionService，DeviceDetailViewModel 自动响应。
        HomeViewModel.ViewDeviceDetailRequested += OnViewDeviceDetailRequested;
        // 主页"工单管理"请求：跳转到工单管理页。
        HomeViewModel.ViewWorkOrderManagerRequested += OnViewWorkOrderManagerRequested;
        DeviceDetailViewModel.ViewAlarmHistoryRequested += OnViewAlarmHistoryRequested;
        AlarmCenterViewModel.ViewAlarmHistoryRequested += OnViewAlarmHistoryRequested;
    }

    /// <summary>
    /// 释放事件订阅，避免事件泄漏。
    /// 多次调用安全：_disposed 守卫防止重复取消订阅触发空引用或逻辑异常。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ConnectionManager.ConnectionStateChanged -= OnConnectionStateChanged;
        ConnectionManager.PropertyChanged -= OnConnectionManagerPropertyChanged;
        ProductionLineViewModel.FocusDeviceRequested -= OnFocusDeviceRequested;
        OverviewViewModel.FocusDeviceRequested -= OnFocusDeviceRequested;
        DeviceDetailViewModel.GoBackRequested -= OnGoBackRequested;
        HomeViewModel.ViewDeviceDetailRequested -= OnViewDeviceDetailRequested;
        HomeViewModel.ViewWorkOrderManagerRequested -= OnViewWorkOrderManagerRequested;
        DeviceDetailViewModel.ViewAlarmHistoryRequested -= OnViewAlarmHistoryRequested;
        AlarmCenterViewModel.ViewAlarmHistoryRequested -= OnViewAlarmHistoryRequested;
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

    /// <summary>产线页/概览页"跳转主页"请求。</summary>
    private void OnFocusDeviceRequested(string _) => Navigate("Home");

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
                        Message = $"采集服务已断开（{e.IpAddress}）· 第 {e.DisconnectCount} 次重试",
                        ShowDateTime = false,
                    });
                else
                {
                    var dur = e.DisconnectDuration.HasValue
                        ? $"{e.DisconnectDuration.Value.TotalSeconds:F0}s"
                        : "—";
                    Growl.Success(new GrowlInfo
                    {
                        Message = $"采集服务已重连，断线时长 {dur}",
                        ShowDateTime = false,
                    });
                }
                return;
            }

            if (!e.IsConnected)
            {
                Growl.Error(new GrowlInfo
                {
                    Message = $"PLC 已断开（IP:{e.IpAddress}）· 第 {e.DisconnectCount} 次重试",
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
                    Message = $"PLC 已重连，断线时长 {dur}",
                    ShowDateTime = false,
                });
            }
        });
    }

    /// <summary>
    /// SelectedIndex 变更回调：记录切换开始时刻，并在渲染完成后（Background 优先级）
    /// 记录耗时，用于排查切换卡顿。预热流程也会经过此方法。
    /// </summary>
    partial void OnSelectedIndexChanged(int value)
    {
        _navSwitchStartTicks = Stopwatch.GetTimestamp();
        var fromName = GetNavName(_navFromIndex, "初始");
        var toName = GetNavName(value, value.ToString());
        Log.Debug("导航 切换 {From} → {To} 开始", fromName, toName);

        if (value == NavigationPageCatalog.DeviceDetail.Index)
            DeviceDetailViewModel.RefreshOnEnter();

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
        if (int.TryParse(indexStr, out var idx) && PageDefinitions.Any(page => page.ShowInSidebar && page.Index == idx))
            SelectedIndex = idx;
    }
}
