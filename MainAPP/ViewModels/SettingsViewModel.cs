using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MainAPP.Resources;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LicenseManager.Models;
using LicenseManager.Services;
using LicenseManager.ViewModels;
using LicenseManager.Views;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Helpers;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Mapping;
using MainAPP.Services;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MainAPP.ViewModels;

/// <summary>
/// 设置视图模型
/// </summary>
public partial class SettingsViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable, INavigationPageLifecycle
{
    /// <summary>
    /// 应用全局设置
    /// </summary>
    public AppSettings AppSettings { get; }
    public AppSettings DraftSettings { get; private set; }

    private readonly IPlcConnectionManager _connectionManager;
    private readonly IDialogService _dialog;
    private readonly LicenseGate _licenseGate;
    private readonly IServiceProvider _services;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly IPlcRuntimeSessionManager? _runtimeSessions;

    /// <summary>
    /// 上次已保存的 PLC 配置（用于检测本次保存是否改变连接参数）
    /// </summary>
    private string _lastSavedPlcConfigSignature = string.Empty;
    private SettingsAuditSnapshot _lastSavedAuditSnapshot = new(string.Empty, 0, 0);
    private PlcBrand _draftBrand;

    /// <summary>上次已保存的界面语言（检测本次保存是否变更语言 → 提示重启生效）。</summary>
    private string _lastSavedLanguageCode = LocalizationCatalog.DefaultLanguage;

    /// <summary>由 CSV 表头动态生成的语言选项；不再把语言列表写死在 XAML 中。</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
        LocalizationCatalog.LanguageCodes
            .Select(code => new LanguageOption(code, GetLanguageDisplayName(code)))
            .ToArray();

    public sealed record LanguageOption(string Code, string DisplayName);

    private static string GetLanguageDisplayName(string code)
    {
        var displayCulture = CultureInfo.CurrentUICulture.Name;
        var resourceName = LocalizationCatalog.Get("Wpf", "Language_" + code.Replace('-', '_'), displayCulture);
        if (!string.IsNullOrWhiteSpace(resourceName)) return resourceName;
        try { return CultureInfo.GetCultureInfo(code).NativeName; }
        catch (CultureNotFoundException) { return code; }
    }

    /// <summary>上次已保存的数据采集模式（用于危险操作确认：DataMode 变化需二次确认）。</summary>
    private KanbanDataMode _lastSavedDataMode;

    /// <summary>上次已保存的运行模式（用于危险操作确认：RunMode 变化需二次确认）。</summary>
    private KanbanRunMode _lastSavedRunMode;

    /// <summary>授权状态定时刷新器（UI 线程 DispatcherTimer，每 60 秒）。</summary>
    private PageRefreshTimer? _licenseRefreshTimer;

    /// <summary>当前用户会话（用于权限门禁）；测试宿主未注册时为 null。</summary>
    private readonly UserSession? _userSession;

    public IReadOnlyList<PlcBrand> PlcBrands { get; } = Enum.GetValues<PlcBrand>();
    public IReadOnlyList<PlcDataFormat> PlcDataFormats { get; } = Enum.GetValues<PlcDataFormat>();
    public IReadOnlyList<string> SiemensModels { get; } = ["S1200", "S1500", "S300", "S400", "S200Smart", "S200"];

    /// <summary>
    /// 上次已保存的班次配置签名（用于检测本次保存是否改变班次配置）
    /// 班次配置变更会立即影响 PlcDataAcquisitionService.DetectShiftChange，
    /// 可能导致班次进行中被意外触发 ResetShift 清零累计数据。
    /// 使用 <see cref="ShiftIdentifier"/> record 列表，按值相等比较，避免字符串拼接
    /// （旧实现 "Name|Start|End" 在班次名含 "|" 字符时误判）。
    /// </summary>
    private IReadOnlyList<ShiftIdentifier> _lastSavedShiftsSignature = Array.Empty<ShiftIdentifier>();

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>设置页统一的页面内操作反馈。</summary>
    public OperationFeedback Feedback { get; } = new();

    [ObservableProperty]
    private bool _collectorSyncPending;

    [ObservableProperty]
    private string _collectorSyncStatus = string.Empty;

    private readonly CancellationTokenSource _disposeCts = new();

    public string UnsavedChangesText => HasUnsavedChanges ? Strings.M074 : Strings.M075;

    partial void OnHasUnsavedChangesChanged(bool value)
    {
        if (IsSaving) return;
        if (value) Feedback.Warning(Strings.Ux_StatusUnsaved);
        else if (Feedback.Kind != OperationFeedbackKind.Error)
            Feedback.Success(Strings.Ux_StatusSaved);
    }

    public bool IsPlcConnected => _connectionManager.IsConnected;
    public string PlcConnectionStatus => _connectionManager.ConnectionStatus;

    // ──────────── 测试连接（[08]）────────────

    /// <summary>测试连接中（按钮 disable + 显示 Loading 文案）。</summary>
    [ObservableProperty]
    private bool _isTestingConnection;

    public string TestConnectionButtonText => IsTestingConnection ? Strings.M076 : Strings.M077;

    // ──────────── 数据源与运行模式（[连接向导]）────────────

    /// <summary>数据采集模式（草稿值）：Local=本进程采集 / Remote=连接采集服务。</summary>
    public KanbanDataMode SelectedDataMode
    {
        get => DraftSettings.DataMode;
        set
        {
            if (DraftSettings.DataMode == value) return;
            DraftSettings.DataMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLocalMode));
            OnPropertyChanged(nameof(IsRemoteMode));
        }
    }

    public bool IsLocalMode
    {
        get => SelectedDataMode == KanbanDataMode.Local;
        set { if (value) SelectedDataMode = KanbanDataMode.Local; }
    }

    public bool IsRemoteMode
    {
        get => SelectedDataMode == KanbanDataMode.Remote;
        set { if (value) SelectedDataMode = KanbanDataMode.Remote; }
    }

    /// <summary>运行模式（草稿值）：Full=展示+管理 / Viewer=仅大屏展示。</summary>
    public KanbanRunMode SelectedRunMode
    {
        get => DraftSettings.RunMode;
        set
        {
            if (DraftSettings.RunMode == value) return;
            DraftSettings.RunMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFullMode));
            OnPropertyChanged(nameof(IsViewerMode));
        }
    }

    public bool IsFullMode
    {
        get => SelectedRunMode == KanbanRunMode.Full;
        set { if (value) SelectedRunMode = KanbanRunMode.Full; }
    }

    public bool IsViewerMode
    {
        get => SelectedRunMode == KanbanRunMode.Viewer;
        set { if (value) SelectedRunMode = KanbanRunMode.Viewer; }
    }

    /// <summary>测试采集服务连接中。</summary>
    [ObservableProperty]
    private bool _isTestingCollectorConnection;

    public string TestCollectorButtonText => IsTestingCollectorConnection ? Strings.M076 : Strings.M077;

    /// <summary>测试采集服务连接结果文案。</summary>
    [ObservableProperty]
    private string _collectorTestResult = string.Empty;

    /// <summary>测试采集服务连接结果类型（"Success"/"Error"/"None"）。</summary>
    [ObservableProperty]
    private string _collectorTestResultType = "None";

    partial void OnIsTestingCollectorConnectionChanged(bool value)
    {
        TestCollectorConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TestCollectorButtonText));
    }

    /// <summary>
    /// 测试采集服务连接：用草稿中的 CollectorHubUrl 建立一次 SignalR 连接（5 秒超时），
    /// 连接成功即服务可达。不影响主链路（KanbanDataClient 单例）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestCollectorConnection))]
    private async Task TestCollectorConnectionAsync()
    {
        var url = DraftSettings.CollectorHubUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            CollectorTestResult = Strings.M064;
            CollectorTestResultType = "Error";
            return;
        }

        IsTestingCollectorConnection = true;
        CollectorTestResult = string.Format(Strings.F158, url);
        CollectorTestResultType = "None";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var builder = new HubConnectionBuilder().WithUrl(url);
            await using var connection = builder.Build();
            await connection.StartAsync(cts.Token);
            CollectorTestResult = Strings.M065;
            CollectorTestResultType = "Success";
        }
        catch (Exception ex)
        {
            CollectorTestResult = string.Format(Strings.F222, ex.Message);
            CollectorTestResultType = "Error";
        }
        finally
        {
            IsTestingCollectorConnection = false;
        }
    }

    private bool CanTestCollectorConnection() => !IsTestingCollectorConnection;

    // ──────────── 保存反馈 ────────────

    /// <summary>保存中（按钮 disable + 显示 "保存中..." 文案）。</summary>
    [ObservableProperty]
    private bool _isSaving;

    public string SaveButtonText => IsSaving ? Strings.M078 : Strings.M079;

    /// <summary>IsSaving 变化时刷新 SaveCommand CanExecute。</summary>
    partial void OnIsSavingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveButtonText));
    }

    partial void OnIsTestingConnectionChanged(bool value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TestConnectionButtonText));
    }

    /// <summary>测试连接结果文案（成功/失败/超时详情）。</summary>
    [ObservableProperty]
    private string? _testConnectionResult;

    /// <summary>测试连接结果类型（"Success"/"Error"/"None"，供 UI DataTrigger 着色）。</summary>
    [ObservableProperty]
    private string _testConnectionResultType = "None";

    /// <summary>
    /// 测试 PLC 连接：用当前文本框中的 IP/端口（未保存值）创建临时驱动尝试连接，5 秒超时。
    /// 不影响主采集连接（PlcConnectionManager），测试完毕立即断开+释放临时驱动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        var ip = DraftSettings.PlcConfig.IpAddress;
        var port = DraftSettings.PlcConfig.Port;

        // 前置校验（复用 Save 的校验逻辑，避免无效参数发起网络请求）
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _))
        {
            TestConnectionResult = Strings.M299;
            TestConnectionResultType = "Error";
            return;
        }
        if (port < 1 || port > 65535)
        {
            TestConnectionResult = string.Format(Strings.F179, port);
            TestConnectionResultType = "Error";
            return;
        }

        IsTestingConnection = true;
        TestConnectionResult = string.Format(Strings.F157, ip, port);
        TestConnectionResultType = "None";

        try
        {
            // 5 秒超时：HslCommunication 默认超时较长，UI 场景需快速反馈
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connectTask = Task.Run(() =>
            {
                var testConfig = ClonePlcConfig(DraftSettings.PlcConfig);
                var factory = _services.GetRequiredService<ISharedPlcDriverFactory>();
                using var driver = factory.Create(testConfig);
                var r = driver.Connect();
                if (r.IsSuccess) driver.Disconnect();
                return r;
            });
            var completed = await Task.WhenAny(connectTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
            if (completed != connectTask)
                throw new OperationCanceledException(cts.Token);
            var result = await connectTask;

            if (result.IsSuccess)
            {
                TestConnectionResult = string.Format(Strings.F225, ip, port);
                TestConnectionResultType = "Success";
            }
            else if (IsSingleConnectionRefused(ip, port, result))
            {
                // 目标 PLC 单连接限制：连接被拒绝（TCP RST/10061）且主采集正连着同一端点
                // → 极可能是 PLC 并发连接数上限（如 S7-1200 默认仅 1 个 S7 连接），
                //   给出可执行提示而不是黑盒的"目标计算机拒绝"。
                TestConnectionResult = Strings.K636;
                TestConnectionResultType = "Error";
            }
            else
            {
                TestConnectionResult = string.Format(Strings.F223, result.Message);
                TestConnectionResultType = "Error";
            }
        }
        catch (OperationCanceledException)
        {
            TestConnectionResult = Strings.F226;
            TestConnectionResultType = "Error";
        }
        catch (Exception ex)
        {
            TestConnectionResult = string.Format(Strings.F224, ex.Message);
            TestConnectionResultType = "Error";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private bool CanTestConnection() => !IsTestingConnection;

    /// <summary>
    /// 单连接限制判定：连接被拒绝（ConnectionLost，如 TCP RST/WSAECONNREFUSED）
    /// 且主采集链路当前正连接同一 IP:Port——目标 PLC 极可能只允许一个并发连接。
    /// </summary>
    private bool IsSingleConnectionRefused(string ip, int port, PlcOperationResult result)
    {
        if (result.ErrorKind != PlcErrorKind.ConnectionLost || !_connectionManager.IsConnected)
            return false;
        var running = AppSettings.PlcConfig;
        return string.Equals(running.IpAddress, ip, StringComparison.OrdinalIgnoreCase) && running.Port == port;
    }

    /// <summary>
    /// 构造函数
    /// </summary>
    public SettingsViewModel(
        AppSettings appSettings,
        IPlcConnectionManager connectionManager,
        IDialogService dialog,
        LicenseGate licenseGate,
        IServiceProvider services,
        IPlcRuntimeProfileProvider? profileProvider = null,
        IPlcRuntimeSessionManager? runtimeSessions = null)
    {
        AppSettings = appSettings;
        _connectionManager = connectionManager;
        _dialog = dialog;
        _licenseGate = licenseGate;
        _services = services;
        _profileProvider = profileProvider;
        _runtimeSessions = runtimeSessions;
        _userSession = services.GetService<UserSession>();
        DraftSettings = CloneSettings(appSettings);
        _draftBrand = DraftSettings.PlcConfig.Brand;
        _lastSavedPlcConfigSignature = GetPlcConfigSignature(DraftSettings.PlcConfig);
        _lastSavedAuditSnapshot = CreateAuditSnapshot(DraftSettings);
        _lastSavedShiftsSignature = GetShiftsSignature(DraftSettings);
        _lastSavedLanguageCode = DraftSettings.EffectiveLanguageCode;
        _lastSavedDataMode = DraftSettings.DataMode;
        _lastSavedRunMode = DraftSettings.RunMode;
        WireDraftEvents();
        _connectionManager.PropertyChanged += OnConnectionPropertyChanged;
    }

    /// <inheritdoc />
    public void OnPageEnter()
    {
        RefreshLicenseStatus(recheck: false);
        StartLicenseStatusTimer();
    }

    /// <inheritdoc />
    public void OnPageExit() => StopLicenseStatusTimer();

    private sealed record SettingsAuditSnapshot(string PlcIp, int PlcPort, int ShiftCount);

    private static SettingsAuditSnapshot CreateAuditSnapshot(AppSettings settings)
        => new(settings.PlcConfig.IpAddress, settings.PlcConfig.Port, settings.Shifts.Count);

    private static AppSettings CloneSettings(AppSettings source)
    {
        var json = JsonSerializer.Serialize(source, AppSettings.JsonOptions);
        var clone = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions) ?? new AppSettings();
        clone.LanguageCode = source.EffectiveLanguageCode;
        return clone;
    }

    private void OnDraftSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.UiScale))
            MainAPP.FontSizeManager.ApplyScale(DraftSettings.UiScale);
        MarkDraftDirty();
    }

    private void OnDraftNestedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is PlcConfig plcConfig && e.PropertyName == nameof(PlcConfig.Brand))
        {
            var oldDefaultPort = PlcConfig.GetDefaultPort(_draftBrand);
            if (plcConfig.Port == oldDefaultPort)
                plcConfig.Port = PlcConfig.GetDefaultPort(plcConfig.Brand);
            _draftBrand = plcConfig.Brand;
        }
        MarkDraftDirty();
    }

    private void OnDraftShiftsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ShiftConfig shift in e.OldItems)
                shift.PropertyChanged -= OnDraftNestedPropertyChanged;
        if (e.NewItems != null)
            foreach (ShiftConfig shift in e.NewItems)
                shift.PropertyChanged += OnDraftNestedPropertyChanged;
        MarkDraftDirty();
    }

    private void MarkDraftDirty()
    {
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(UnsavedChangesText));
    }

    private void WireDraftEvents()
    {
        DraftSettings.PropertyChanged += OnDraftSettingsChanged;
        DraftSettings.PlcConfig.PropertyChanged += OnDraftNestedPropertyChanged;
        DraftSettings.Shifts.CollectionChanged += OnDraftShiftsChanged;
        foreach (var shift in DraftSettings.Shifts)
            shift.PropertyChanged += OnDraftNestedPropertyChanged;
    }

    private void UnwireDraftEvents()
    {
        DraftSettings.PropertyChanged -= OnDraftSettingsChanged;
        DraftSettings.PlcConfig.PropertyChanged -= OnDraftNestedPropertyChanged;
        DraftSettings.Shifts.CollectionChanged -= OnDraftShiftsChanged;
        foreach (var shift in DraftSettings.Shifts)
            shift.PropertyChanged -= OnDraftNestedPropertyChanged;
    }

    /// <summary>
    /// 释放事件订阅，避免事件泄漏。
    /// 构造时订阅了 PlcConnectionManager.PropertyChanged 及 DraftSettings 的事件树，
    /// 释放时统一解绑（DraftSettings 解绑复用 UnwireDraftEvents）。
    /// </summary>
    public void Dispose()
    {
        _disposeCts.Cancel();
        _connectionManager.PropertyChanged -= OnConnectionPropertyChanged;
        UnwireDraftEvents();
        StopLicenseStatusTimer();
        _disposeCts.Dispose();
    }

    /// <summary>启动授权状态定时刷新（60 秒）。仅在有 UI 调度器的宿主下启动。</summary>
    private void StartLicenseStatusTimer()
    {
        if (Application.Current is null) return;
        _licenseRefreshTimer = new PageRefreshTimer(TimeSpan.FromSeconds(60), () => RefreshLicenseStatus(recheck: false));
        _licenseRefreshTimer.Start();
    }

    /// <summary>停止授权状态定时刷新（释放时调用）。</summary>
    private void StopLicenseStatusTimer()
    {
        if (_licenseRefreshTimer is null) return;
        _licenseRefreshTimer.Dispose();
        _licenseRefreshTimer = null;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlcConnectionManager.IsConnected))
            OnPropertyChanged(nameof(IsPlcConnected));
        if (e.PropertyName == nameof(PlcConnectionManager.ConnectionStatus))
            OnPropertyChanged(nameof(PlcConnectionStatus));
    }

    // ──────────── 授权管理 ────────────

    /// <summary>当前授权状态枚举（UI 用 DataTrigger 切换颜色/文本）</summary>
    public LicenseStatus LicenseStatus => _licenseGate.CurrentStatus;

    /// <summary>当前机器码（8 字符，用户复制给管理员获取激活码）</summary>
    public string MachineCode => _licenseGate.MachineCode;

    /// <summary>当前激活信息（已激活时非 null）</summary>
    public LicenseInfo? CurrentLicense => _licenseGate.CurrentLicense;

    /// <summary>试用剩余天数（试用期内显示）</summary>
    public int? RemainingTrialDays => _licenseGate.RemainingTrialDays;

    /// <summary>授权状态显示文本</summary>
    public string LicenseStatusText
    {
        get
        {
            var status = _licenseGate.CurrentStatus;
            return status switch
            {
                LicenseStatus.Active when _licenseGate.CurrentLicense?.IsPermanent == false
                    => string.Format(Strings.F113, _licenseGate.CurrentLicense.ExpireDate),
                LicenseStatus.Active => Strings.License_ActivePermanent,
                LicenseStatus.Trial => string.Format(Strings.F212, RemainingTrialDays ?? 0),
                LicenseStatus.TrialExpired => Strings.License_TrialExpired,
                LicenseStatus.TrialManipulated => Strings.License_TrialManipulated,
                LicenseStatus.Expired => Strings.License_Expired,
                LicenseStatus.MachineMismatch => Strings.License_MachineMismatch,
                _ => Strings.License_Inactive,
            };
        }
    }

    /// <summary>授权类型描述（永久/限期/试用）</summary>
    public string LicenseTypeText
    {
        get
        {
            var status = _licenseGate.CurrentStatus;
            if (status == LicenseStatus.Active && _licenseGate.CurrentLicense != null)
                return _licenseGate.CurrentLicense.IsPermanent ? Strings.M293 : Strings.M294;
            if (status == LicenseStatus.Trial) return Strings.M015;
            return "—";
        }
    }

    /// <summary>激活时间显示（已激活时返回字符串，否则 "—"）</summary>
    public string ActivatedAtText
    {
        get
        {
            if (_licenseGate.CurrentLicense == null) return "—";
            return _licenseGate.CurrentLicense.ActivatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>到期时间显示（已激活时返回字符串，永久授权返回"永久"，否则 "—"）</summary>
    public string ExpireDateText
    {
        get
        {
            if (_licenseGate.CurrentLicense == null) return "—";
            return _licenseGate.CurrentLicense.IsPermanent
                ? Strings.M339
                : _licenseGate.CurrentLicense.ExpireDate!.Value.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    /// <summary>激活码显示（已激活时返回格式化激活码，否则 "—"）。用户可查看/备份自己的激活码。</summary>
    /// <remarks>默认打码（前 4 后 4，中间 ****），经 <see cref="IsProductKeyMasked"/> 切换明文。</remarks>
    public string ProductKeyText
    {
        get
        {
            if (_licenseGate.CurrentLicense == null) return "—";
            var key = _licenseGate.CurrentLicense.ProductKey;
            if (!IsProductKeyMasked || string.IsNullOrEmpty(key)) return key;
            return key.Length <= 8
                ? new string('*', key.Length)
                : key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
        }
    }

    /// <summary>激活码是否打码显示（默认 true）。</summary>
    [ObservableProperty]
    private bool _isProductKeyMasked = true;

    /// <summary>「显示/隐藏」按钮文案。</summary>
    public string ProductKeyToggleText => IsProductKeyMasked ? Strings.Common_Show : Strings.Common_Hide;

    partial void OnIsProductKeyMaskedChanged(bool value)
    {
        OnPropertyChanged(nameof(ProductKeyText));
        OnPropertyChanged(nameof(ProductKeyToggleText));
    }

    /// <summary>切换激活码打码/明文显示。</summary>
    [RelayCommand]
    private void ToggleProductKeyMask() => IsProductKeyMasked = !IsProductKeyMasked;

    /// <summary>刷新授权状态属性通知（激活成功后调用）</summary>
    private void RefreshLicenseStatus(bool recheck)
    {
        if (recheck) _licenseGate.CheckStatus();
        else _licenseGate.RefreshStatusReadOnly();
        OnPropertyChanged(nameof(LicenseStatus));
        OnPropertyChanged(nameof(MachineCode));
        OnPropertyChanged(nameof(CurrentLicense));
        OnPropertyChanged(nameof(RemainingTrialDays));
        OnPropertyChanged(nameof(LicenseStatusText));
        OnPropertyChanged(nameof(LicenseTypeText));
        OnPropertyChanged(nameof(ActivatedAtText));
        OnPropertyChanged(nameof(ExpireDateText));
        OnPropertyChanged(nameof(ProductKeyText));
    }

    /// <summary>
    /// 重新激活命令：弹出激活对话框。
    /// 激活成功后刷新授权状态，并通过 MainWindowViewModel 刷新侧边栏状态。
    /// </summary>
    [RelayCommand]
    private void Reactivate()
    {
        if (!EnsureAdmin()) return;

        var activationVm = _services.GetRequiredService<ActivationViewModel>();
        var dialog = new ActivationDialog(activationVm);

        // 根据当前状态设置提示消息
        activationVm.StatusMessage = _licenseGate.CurrentStatus switch
        {
            LicenseStatus.Active => Strings.M340,
            LicenseStatus.Trial => string.Format(Strings.F213, RemainingTrialDays ?? 0),
            LicenseStatus.TrialExpired => Strings.M341,
            LicenseStatus.Expired => Strings.M342,
            LicenseStatus.MachineMismatch => Strings.M343,
            _ => Strings.M344,
        };

        if (dialog.ShowDialog() == true)
        {
            // 激活成功 → 刷新本页授权信息 + 通知主窗口刷新侧边栏状态
            RefreshLicenseStatus(recheck: true);
            NotifyMainWindowLicenseChanged();
            _dialog.NotifySuccess(Strings.M016);
        }
    }

    /// <summary>
    /// 复制机器码到剪贴板（用户将机器码发给管理员以获取激活码）。
    /// </summary>
    [RelayCommand]
    private void CopyMachineCode()
    {
        try
        {
            Clipboard.SetText(_licenseGate.MachineCode);
            _dialog.NotifySuccess(Strings.M017);
        }
        catch
        {
            _dialog.NotifyWarning(Strings.M018);
        }
    }

    /// <summary>
    /// 复制激活码到剪贴板（U-1：用户备份/迁移时使用）。
    /// </summary>
    [RelayCommand]
    private void CopyProductKey()
    {
        var key = _licenseGate.CurrentLicense?.ProductKey;
        if (string.IsNullOrEmpty(key))
        {
            _dialog.NotifyWarning(Strings.M019);
            return;
        }

        try
        {
            Clipboard.SetText(key);
            _dialog.NotifySuccess(Strings.M020);
        }
        catch
        {
            _dialog.NotifyWarning(Strings.M021);
        }
    }

    /// <summary>
    /// 通知 MainWindowViewModel 刷新侧边栏授权状态显示。
    /// 通过 IServiceProvider 解析 MainWindowViewModel（避免循环依赖）。
    /// </summary>
    private void NotifyMainWindowLicenseChanged()
    {
        try
        {
            var mainVm = _services.GetService<MainWindowViewModel>();
            mainVm?.RefreshLicenseStatus();
        }
        catch
        {
            // 测试环境或 MainWindowViewModel 未注册时静默忽略
        }
    }

    /// <summary>
    /// 生成班次配置签名（用于检测班次配置是否变化）。
    /// 返回 <see cref="ShiftIdentifier"/> record 列表，调用方用 SequenceEqual 比较，
    /// record 按值相等保证 Name/StartTime/EndTime 任一变化都能被检测到。
    /// </summary>
        private static IReadOnlyList<ShiftIdentifier> GetShiftsSignature(AppSettings settings)
    {
            // Shifts 配置异常缺失时回退空列表，避免 Select 抛 NRE
            var shifts = settings.Shifts ?? new System.Collections.ObjectModel.ObservableCollection<ShiftConfig>();
        return shifts.Select(ShiftIdentifier.From).Where(s => s != null).ToList()!;
    }

    /// <summary>
    /// 新增班次
    /// </summary>
    [RelayCommand]
    private void AddShift()
    {
        DraftSettings.Shifts.Add(new ShiftConfig { Name = string.Format(Strings.F166, DraftSettings.Shifts.Count + 1) });
    }

    /// <summary>
    /// 删除指定班次
    /// </summary>
    [RelayCommand]
    private void RemoveShift(ShiftConfig? shift)
    {
        if (shift == null) return;
        if (DraftSettings.Shifts.Count <= 1)
        {
            _dialog.NotifyInfo(Strings.M022);
            return;
        }
        DraftSettings.Shifts.Remove(shift);
    }

    /// <summary>
    /// 保存设置命令（含输入校验）。
    /// - 若 PLC 连接参数变化：保存后主动断开当前连接，让采集循环下次轮询时用新配置重连。
    /// - 若班次配置变化：提示用户"修改将在下个班次生效"，避免班次进行中立即触发 ResetShift 清零数据。
    ///   提示后用户可选择接受（保存生效，当前班次可能被中断）或取消（不保存）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var error = Validate(DraftSettings);
        if (error != null)
        {
            Feedback.Error(error);
            _dialog.NotifyWarning(error);
            return;
        }

        var plcConfigChanged = GetPlcConfigSignature(DraftSettings.PlcConfig) != _lastSavedPlcConfigSignature;
        var dataModeChanged = DraftSettings.DataMode != _lastSavedDataMode;
        var runModeChanged = DraftSettings.RunMode != _lastSavedRunMode;
        var auditBefore = _lastSavedAuditSnapshot;
        var auditAfter = CreateAuditSnapshot(DraftSettings);

        // 权限门禁（下沉到危险项）：非管理员可保存主题/语言/标题等无害设置，但 PLC/数据源/运行模式需管理员
        if (_userSession is { IsAdmin: false } && (plcConfigChanged || dataModeChanged || runModeChanged))
        {
            _dialog.NotifyWarning(Strings.M337);
            return;
        }

        // 检测班次配置是否变化（用 SequenceEqual 比较两个 record 列表）
        var currentShiftsSig = GetShiftsSignature(DraftSettings);
        var shiftsChanged = !currentShiftsSig.SequenceEqual(_lastSavedShiftsSignature);

        // 危险操作确认：PLC 连接参数 / 数据源 / 运行模式变化会断开连接或切换运行模式，需二次确认。
        if (plcConfigChanged || dataModeChanged || runModeChanged)
        {
            var dangerResult = _dialog.Show(
                BuildDangerConfirmation(plcConfigChanged, dataModeChanged, runModeChanged),
                Strings.Settings_DangerConfirmTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (dangerResult != MessageBoxResult.Yes) return;
        }

        // 班次配置变化时提示用户：修改将立即生效，可能导致当前班次被中断。
        // 使用 HC MessageBox（深色主题）进行 YesNo 确认，返回 MessageBoxResult 与原 API 一致。
        if (shiftsChanged)
        {
            var result = _dialog.Show(
                Strings.F_ShiftConfigChanged,
                Strings.M_ShiftConfigChangedTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        IsSaving = true;
        Feedback.Working(Strings.Ux_StatusSaving);
        try
        {
            CopySettings(DraftSettings, AppSettings);
            AppSettings.Save();
            if (_runtimeSessions is not null)
                _runtimeSessions.RefreshFromSettings();
            else
                _profileProvider?.Refresh(AppSettings.PlcConfig);
            // 前后值摘要：只取关键字段，PLC 密码/完整配置不落审计库
            AuditLog.Record("Settings.Update", "Settings", null,
                before: auditBefore,
                after: auditAfter,
                detail: $"PLC={DraftSettings.PlcConfig.IpAddress}:{DraftSettings.PlcConfig.Port} 班次={DraftSettings.Shifts.Count}");
            _lastSavedAuditSnapshot = auditAfter;

            if (plcConfigChanged)
            {
                _lastSavedPlcConfigSignature = GetPlcConfigSignature(DraftSettings.PlcConfig);
                // 无 keyed session 时保留旧连接 facade 的强制断开行为。
                if (_runtimeSessions is null)
                    _connectionManager.Disconnect();
            }

            if (shiftsChanged)
            {
                _lastSavedShiftsSignature = currentShiftsSig;
            }

            _lastSavedDataMode = DraftSettings.DataMode;
            _lastSavedRunMode = DraftSettings.RunMode;

            HasUnsavedChanges = false;
            OnPropertyChanged(nameof(UnsavedChangesText));
            Feedback.Success(Strings.Ux_StatusSaved);

            _dialog.NotifySuccess(Strings.M023);
            // 语言切换：保存到 settings.json，需重启后经 App 启动应用 CultureInfo 生效
            if (!string.Equals(DraftSettings.EffectiveLanguageCode, _lastSavedLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                _lastSavedLanguageCode = DraftSettings.EffectiveLanguageCode;
                _dialog.NotifyInfo(Resources.Strings.Common_RestartRequired);
            }
            var syncResult = await SyncCollectorSettingsAsync(_disposeCts.Token); // Remote 模式：采集参数同步到 Collector（热生效）
            var remoteMode = _services.GetService<IRuntimeMode>()?.IsRemote == true;
            CollectorSyncPending = remoteMode && (!syncResult.WasAttempted || !syncResult.IsSuccess);
            CollectorSyncStatus = !remoteMode ? Strings.Settings_CollectorLocalMode : syncResult.IsSuccess ? Strings.Settings_CollectorConfirmed : Strings.Settings_CollectorPending;
            if (CollectorSyncPending)
            {
                HasUnsavedChanges = true;
                OnPropertyChanged(nameof(UnsavedChangesText));
                Feedback.Warning(syncResult.ErrorMessage ?? Strings.Settings_CollectorNotConnectedPending);
                _dialog.NotifyWarning(syncResult.ErrorMessage ?? Strings.Settings_CollectorNotConnectedPending);
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(string.Format(Strings.F066, ex.Message));
            _dialog.NotifyError(string.Format(Strings.F066, ex.Message));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving;

    /// <summary>
    /// 权限门禁：设置页危险操作（PLC 配置 / 恢复默认 / 授权管理）仅管理员可用。
    /// 返回 false 时已通过对话框给出提示。
    /// </summary>
    private bool EnsureAdmin()
    {
        if (_userSession?.IsAdmin == true) return true;
        _dialog.NotifyWarning(Strings.M337);
        return false;
    }

    /// <summary>构造危险操作确认文案：按实际变化项列出后果，避免无变化的项造成误告警。</summary>
    private static string BuildDangerConfirmation(bool plcChanged, bool dataModeChanged, bool runModeChanged)
    {
        var reasons = new List<string>(3);
        if (plcChanged) reasons.Add(Strings.Settings_Warn_PlcChanged);
        if (dataModeChanged) reasons.Add(Strings.Settings_Warn_DataModeChanged);
        if (runModeChanged) reasons.Add(Strings.Settings_Warn_RunModeChanged);
        return string.Join("\n", reasons) + "\n" + Strings.Settings_ConfirmSave;
    }

    /// <summary>
    /// Remote 模式：把采集相关参数同步到 Collector（落 Collector 侧 settings.json 并热生效）。
    /// 解决"Remote 模式下设置改了采集进程无感知"的配置分裂——MainAPP 本地仍写自己的 settings.json
    /// （UI 设置），采集参数经 SignalR 推给 Collector。失败仅提示，不阻断保存。
    /// 全部用 GetService（可空）+ try/catch：测试宿主未注册 Remote 链路时静默跳过。
    /// </summary>
    private async Task<CollectorSyncResult> SyncCollectorSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtimeMode = _services.GetService<IRuntimeMode>();
            if (runtimeMode is null || !runtimeMode.IsRemote) return CollectorSyncResult.NotAttempted;
            var client = _services.GetService<KanbanAdminClient>();
            if (client is null || !client.IsConnected) return CollectorSyncResult.NotAttempted; // 未连接时本地保存仍生效，连接恢复后由用户再保存一次
            var dto = CollectorSettingsMapper.ToDto(AppSettings);
            await client.SaveCollectorSettingsAsync(dto, cancellationToken);
            _dialog?.NotifySuccess(Strings.M024);
            return CollectorSyncResult.Success;
        }
        catch (Exception ex)
        {
            return CollectorSyncResult.Failed(string.Format(Strings.F231, ex.Message));
        }
    }

    private readonly record struct CollectorSyncResult(bool WasAttempted, bool IsSuccess, string? ErrorMessage)
    {
        public static CollectorSyncResult NotAttempted => new(false, false, Strings.Settings_CollectorNotConnected);
        public static CollectorSyncResult Success => new(true, true, null);
        public static CollectorSyncResult Failed(string message) => new(true, false, message);
    }

    [RelayCommand]
    private void CancelChanges()
    {
        ReplaceDraft(CloneSettings(AppSettings));
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(UnsavedChangesText));
        _dialog.NotifyInfo(Strings.M025);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        if (!EnsureAdmin()) return;
        var result = _dialog.Show(Strings.M026, Strings.M_RestoreDefaults, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        ReplaceDraft(new AppSettings());
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(UnsavedChangesText));
        _dialog.NotifyInfo(Strings.M027);
    }

    private void ReplaceDraft(AppSettings settings)
    {
        UnwireDraftEvents();
        settings.LanguageCode = settings.EffectiveLanguageCode;
        DraftSettings = settings;
        _draftBrand = DraftSettings.PlcConfig.Brand;
        WireDraftEvents();
        OnPropertyChanged(nameof(DraftSettings));
        OnPropertyChanged(nameof(SelectedDataMode));
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsRemoteMode));
        OnPropertyChanged(nameof(SelectedRunMode));
        OnPropertyChanged(nameof(IsFullMode));
        OnPropertyChanged(nameof(IsViewerMode));
        // 草稿整体替换后，界面字号预览立即对齐新草稿值（取消/恢复默认均生效）。
        MainAPP.FontSizeManager.ApplyScale(DraftSettings.UiScale);
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        // 审查修复 2026-09-02（P0-2）：整体写入包进 target.UpdateLock——本地模式下采集线程
        // （PlcDataAcquisitionService / PlcScanPipeline / PlcRuntimeSession.RefreshFromSettings）
        // 每轮都读 PollingIntervalMs / HistoryWriteIntervalScans / Batch 参数 / ConnectionProfiles，
        // 原先除 Shifts 外全部裸写：换引用 + 逐字段赋值与采集读取并发，可读到半更新状态，
        // 且 ConnectionProfiles 的"整体换引用"与 PlcRuntimeSession 内部 _sync 锁不互斥（两把锁）。
        // 读侧统一经 Get*Snapshot() 取快照（先 UpdateLock 后各自的内部锁，锁顺序不可反转）。
        lock (target.UpdateLock)
        {
            // 数据源/运行模式/采集服务地址是可编辑设置（SelectedDataMode/SelectedRunMode/TestCollectorConnectionAsync
            // 均读写草稿），漏拷会导致保存后改动被静默丢弃（审查修复 2026-08-13）。
            target.DataMode = source.DataMode;
            target.RunMode = source.RunMode;
            target.CollectorHubUrl = source.CollectorHubUrl;
            // 用快照整体复制，避免逐字段漏拷（如 ModbusTcp.BatchInt32Limit）；快照含全部嵌套 Options。
            target.PlcConfig = source.PlcConfig.CreateSnapshot();
            target.ConnectionProfiles = source.CreateConnectionProfilesSnapshot();
            target.EnsureConnectionProfiles();
            target.PollingIntervalMs = source.PollingIntervalMs;
            target.HistoryWriteIntervalScans = source.HistoryWriteIntervalScans;
            target.PlcBatchReadMaxLength = source.PlcBatchReadMaxLength;
            target.PlcBatchReadMaxGapSlots = source.PlcBatchReadMaxGapSlots;
            target.DashboardRefreshIntervalMs = source.DashboardRefreshIntervalMs;
            target.AppTitle = source.AppTitle;
            target.LanguageCode = source.EffectiveLanguageCode;
            target.IsDarkTheme = source.IsDarkTheme;
            target.UiScale = source.UiScale;
            target.EnableAlarmSound = source.EnableAlarmSound;
            target.EnableAutomaticDailyReport = source.EnableAutomaticDailyReport;
            target.AutomaticDailyReportTime = source.AutomaticDailyReportTime;
            target.AutomaticDailyReportIsMaster = source.AutomaticDailyReportIsMaster;
            // 审查修复 2026-08-13：改为锁内原地更新而非替换集合实例——
            // ①ProductionLineViewModel 等订阅方挂在旧实例上，替换会使其订阅永久失效（僵尸引用）；
            // ②本地模式下采集轮询线程也会枚举 Shifts，与写入方原地更新用同一把锁互斥。
            // 锁顺序：UpdateLock（外）→ ShiftsLock（内），所有写侧保持一致避免死锁。
            lock (target.ShiftsLock)
            {
                target.Shifts.Clear();
                foreach (var s in source.Shifts)
                    target.Shifts.Add(new ShiftConfig { Name = s.Name, StartTime = s.StartTime, EndTime = s.EndTime });
            }
        }
    }

    /// <summary>
    /// 校验所有输入。返回 null 表示通过，否则返回错误消息。
    /// </summary>
    private string? Validate(AppSettings settings)
    {
        // 看板标题校验
        if (string.IsNullOrWhiteSpace(settings.AppTitle))
            return Strings.M028;

        // IP 地址校验
        var ip = settings.PlcConfig.IpAddress;
        if (string.IsNullOrWhiteSpace(ip))
            return Strings.M029;
        if (!IPAddress.TryParse(ip, out var parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetwork)
            return string.Format(Strings.F136, ip);

        // 端口校验
        var port = settings.PlcConfig.Port;
        if (port < 1 || port > 65535)
            return string.Format(Strings.F180, port);

        if (!Enum.IsDefined(settings.PlcConfig.Brand))
            return string.Format(Strings.F056, settings.PlcConfig.Brand);
        if (settings.PlcConfig.TimeoutMs < 100 || settings.PlcConfig.TimeoutMs > 60000)
            return string.Format(Strings.F016, settings.PlcConfig.TimeoutMs);
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && !SiemensModels.Contains(settings.PlcConfig.SiemensModel, StringComparer.OrdinalIgnoreCase))
            return string.Format(Strings.F057, settings.PlcConfig.SiemensModel);
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensRack > 7)
            return string.Format(Strings.F017, settings.PlcConfig.SiemensRack);
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensSlot > 31)
            return string.Format(Strings.F018, settings.PlcConfig.SiemensSlot);
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensBatchInt32Limit is < 1 or > 55)
            return string.Format(Strings.F019, settings.PlcConfig.SiemensBatchInt32Limit);
        if (settings.PlcConfig.Brand == PlcBrand.Omron && settings.PlcConfig.OmronReadSplits is < 1 or > 999)
            return string.Format(Strings.F156, settings.PlcConfig.OmronReadSplits);
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusUnitId is < 1 or > 247)
            return string.Format(Strings.F003, settings.PlcConfig.ModbusUnitId);
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusRegisterFunction is not (3 or 4))
            return string.Format(Strings.F005, settings.PlcConfig.ModbusRegisterFunction);
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusBitFunction is not (1 or 2))
            return string.Format(Strings.F004, settings.PlcConfig.ModbusBitFunction);
        if (!Enum.IsDefined(settings.PlcConfig.ModbusDataFormat) || !Enum.IsDefined(settings.PlcConfig.SiemensDataFormat))
            return Strings.M030;

        // 轮询间隔校验
        var interval = settings.PollingIntervalMs;
        if (interval < 50)
            return string.Format(Strings.F218, interval);

        // 历史写入间隔校验
        var historyInterval = settings.HistoryWriteIntervalScans;
        if (historyInterval < 1)
            return string.Format(Strings.F075, historyInterval);

        if (settings.PlcBatchReadMaxLength < 1 || settings.PlcBatchReadMaxLength > 1024)
            return string.Format(Strings.F014, settings.PlcBatchReadMaxLength);
        if (settings.PlcBatchReadMaxGapSlots < 0 || settings.PlcBatchReadMaxGapSlots > 16)
            return string.Format(Strings.F013, settings.PlcBatchReadMaxGapSlots);

        // 班次配置校验
        var shiftError = ShiftValidator.Validate(settings.Shifts);
        if (shiftError != null) return shiftError;

        return null;
    }

    private static PlcConfig ClonePlcConfig(PlcConfig source) => source.CreateSnapshot();

    private static string GetPlcConfigSignature(PlcConfig config) =>
        $"{config.Brand}|{config.IpAddress}|{config.Port}|{config.TimeoutMs}|{config.SiemensModel}|{config.SiemensRack}|{config.SiemensSlot}|{config.SiemensBatchInt32Limit}|{config.OmronReadSplits}|{config.ModbusUnitId}|{config.ModbusAddressStartWithZero}|{config.ModbusRegisterFunction}|{config.ModbusBitFunction}|{config.ModbusDataFormat}|{config.SiemensDataFormat}";
}
