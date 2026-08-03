using System.Net;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LicenseManager.Models;
using LicenseManager.Services;
using LicenseManager.ViewModels;
using LicenseManager.Views;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MainAPP.ViewModels;

/// <summary>
/// 设置视图模型
/// </summary>
public partial class SettingsViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
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

    /// <summary>
    /// 上次已保存的 PLC 配置（用于检测本次保存是否改变连接参数）
    /// </summary>
    private string _lastSavedPlcConfigSignature = string.Empty;
    private PlcBrand _draftBrand;

    /// <summary>上次已保存的界面语言（检测本次保存是否变更语言 → 提示重启生效）。</summary>
    private AppLanguage _lastSavedLanguage = AppLanguage.Zh;

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

    public string UnsavedChangesText => HasUnsavedChanges ? "有未保存修改" : "已保存";

    public bool IsPlcConnected => _connectionManager.IsConnected;
    public string PlcConnectionStatus => _connectionManager.ConnectionStatus;

    // ──────────── 测试连接（[08]）────────────

    /// <summary>测试连接中（按钮 disable + 显示 Loading 文案）。</summary>
    [ObservableProperty]
    private bool _isTestingConnection;

    public string TestConnectionButtonText => IsTestingConnection ? "测试中..." : "测试连接";

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

    public string TestCollectorButtonText => IsTestingCollectorConnection ? "测试中..." : "测试连接";

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
            CollectorTestResult = "请输入采集服务地址";
            CollectorTestResultType = "Error";
            return;
        }

        IsTestingCollectorConnection = true;
        CollectorTestResult = $"正在连接 {url} …";
        CollectorTestResultType = "None";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var builder = new HubConnectionBuilder().WithUrl(url);
            await using var connection = builder.Build();
            await connection.StartAsync(cts.Token);
            CollectorTestResult = "连接成功（采集服务可达）";
            CollectorTestResultType = "Success";
        }
        catch (Exception ex)
        {
            CollectorTestResult = $"连接失败：{ex.Message}";
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

    public string SaveButtonText => IsSaving ? "保存中..." : "保存设置";

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
            TestConnectionResult = "IP 地址无效";
            TestConnectionResultType = "Error";
            return;
        }
        if (port < 1 || port > 65535)
        {
            TestConnectionResult = $"端口号必须在 1-65535 之间，当前: {port}";
            TestConnectionResultType = "Error";
            return;
        }

        IsTestingConnection = true;
        TestConnectionResult = $"正在连接 {ip}:{port} …";
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
                TestConnectionResult = $"连接成功（{ip}:{port}）";
                TestConnectionResultType = "Success";
            }
            else
            {
                TestConnectionResult = $"连接失败：{result.Message}";
                TestConnectionResultType = "Error";
            }
        }
        catch (OperationCanceledException)
        {
            TestConnectionResult = $"连接超时（5 秒无响应），请检查 IP 和端口";
            TestConnectionResultType = "Error";
        }
        catch (Exception ex)
        {
            TestConnectionResult = $"连接异常：{ex.Message}";
            TestConnectionResultType = "Error";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private bool CanTestConnection() => !IsTestingConnection;

    /// <summary>
    /// 构造函数
    /// </summary>
    public SettingsViewModel(
        AppSettings appSettings,
        IPlcConnectionManager connectionManager,
        IDialogService dialog,
        LicenseGate licenseGate,
        IServiceProvider services,
        IPlcRuntimeProfileProvider? profileProvider = null)
    {
        AppSettings = appSettings;
        _connectionManager = connectionManager;
        _dialog = dialog;
        _licenseGate = licenseGate;
        _services = services;
        _profileProvider = profileProvider;
        DraftSettings = CloneSettings(appSettings);
        _draftBrand = DraftSettings.PlcConfig.Brand;
        _lastSavedPlcConfigSignature = GetPlcConfigSignature(DraftSettings.PlcConfig);
        _lastSavedShiftsSignature = GetShiftsSignature(DraftSettings);
        _lastSavedLanguage = DraftSettings.Language;
        WireDraftEvents();
        _connectionManager.PropertyChanged += OnConnectionPropertyChanged;
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        var json = JsonSerializer.Serialize(source, AppSettings.JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions) ?? new AppSettings();
    }

    private void OnDraftSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => MarkDraftDirty();

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
        _connectionManager.PropertyChanged -= OnConnectionPropertyChanged;
        UnwireDraftEvents();
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlcConnectionManager.IsConnected))
            OnPropertyChanged(nameof(IsPlcConnected));
        if (e.PropertyName == nameof(PlcConnectionManager.ConnectionStatus))
            OnPropertyChanged(nameof(PlcConnectionStatus));
    }

    // ──────────── 授权管理（P1-2）────────────

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
                    => $"已激活 · 到期 {_licenseGate.CurrentLicense.ExpireDate:yyyy-MM-dd}",
                LicenseStatus.Active => "已激活 · 永久授权",
                LicenseStatus.Trial => $"试用期内 · 剩 {RemainingTrialDays ?? 0} 天",
                LicenseStatus.TrialExpired => "试用期已过期",
                LicenseStatus.TrialManipulated => "试用期异常（检测到时间篡改）",
                LicenseStatus.Expired => "授权已过期",
                LicenseStatus.MachineMismatch => "授权与当前机器不匹配",
                _ => "未激活",
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
                return _licenseGate.CurrentLicense.IsPermanent ? "永久授权" : "限期授权";
            if (status == LicenseStatus.Trial) return "试用授权";
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
                ? "永久"
                : _licenseGate.CurrentLicense.ExpireDate!.Value.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    /// <summary>激活码显示（已激活时返回格式化激活码，否则 "—"）。用户可查看/备份自己的激活码。</summary>
    public string ProductKeyText
    {
        get
        {
            if (_licenseGate.CurrentLicense == null) return "—";
            return _licenseGate.CurrentLicense.ProductKey;
        }
    }

    /// <summary>刷新授权状态属性通知（激活成功后调用）</summary>
    private void RefreshLicenseStatus()
    {
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
        var activationVm = _services.GetRequiredService<ActivationViewModel>();
        var dialog = new ActivationDialog(activationVm);

        // 根据当前状态设置提示消息
        activationVm.StatusMessage = _licenseGate.CurrentStatus switch
        {
            LicenseStatus.Active => "输入新的激活码以替换当前授权。",
            LicenseStatus.Trial => $"试用期内，剩余 {RemainingTrialDays ?? 0} 天。输入激活码以完成授权。",
            LicenseStatus.TrialExpired => "试用期已过期，请输入激活码继续使用。",
            LicenseStatus.Expired => "授权已过期，请输入新的激活码。",
            LicenseStatus.MachineMismatch => "授权与当前机器不匹配，请重新激活。",
            _ => "请输入激活码以继续使用。",
        };

        if (dialog.ShowDialog() == true)
        {
            // 激活成功 → 刷新本页授权信息 + 通知主窗口刷新侧边栏状态
            RefreshLicenseStatus();
            NotifyMainWindowLicenseChanged();
            _dialog.NotifySuccess("激活成功");
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
            _dialog.NotifySuccess("机器码已复制到剪贴板");
        }
        catch
        {
            _dialog.NotifyWarning("复制失败，请手动记录机器码");
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
            _dialog.NotifyWarning("当前未激活，无激活码可复制");
            return;
        }

        try
        {
            Clipboard.SetText(key);
            _dialog.NotifySuccess("激活码已复制到剪贴板");
        }
        catch
        {
            _dialog.NotifyWarning("复制失败，请手动记录激活码");
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
        DraftSettings.Shifts.Add(new ShiftConfig { Name = $"班次{DraftSettings.Shifts.Count + 1}" });
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
            _dialog.NotifyInfo("至少保留一个班次");
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
    private void Save()
    {
        var error = Validate(DraftSettings);
        if (error != null)
        {
            _dialog.NotifyWarning(error);
            return;
        }

        var currentIp = DraftSettings.PlcConfig.IpAddress;
        var currentPort = DraftSettings.PlcConfig.Port;
        var plcConfigChanged = GetPlcConfigSignature(DraftSettings.PlcConfig) != _lastSavedPlcConfigSignature;

        // 检测班次配置是否变化（用 SequenceEqual 比较两个 record 列表）
        var currentShiftsSig = GetShiftsSignature(DraftSettings);
        var shiftsChanged = !currentShiftsSig.SequenceEqual(_lastSavedShiftsSignature);

        // 班次配置变化时提示用户：修改将立即生效，可能导致当前班次被中断。
        // 使用 HC MessageBox（深色主题）进行 YesNo 确认，返回 MessageBoxResult 与原 API 一致。
        if (shiftsChanged)
        {
            var result = _dialog.Show(
                "班次配置已修改。\n\n" +
                "新配置将立即生效，若当前正处于班次进行中，\n" +
                "可能导致班次切换检测误判并清零当前累计数据。\n\n" +
                "建议在班次切换时刻再修改。\n\n" +
                "是否继续保存？",
                "班次配置变更提示",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        IsSaving = true;
        try
        {
            CopySettings(DraftSettings, AppSettings);
            AppSettings.Save();
            _profileProvider?.Refresh(AppSettings.PlcConfig);

            if (plcConfigChanged)
            {
                _lastSavedPlcConfigSignature = GetPlcConfigSignature(DraftSettings.PlcConfig);
                // 强制断开，触发下次连接使用新参数
                _connectionManager.Disconnect();
            }

            if (shiftsChanged)
            {
                _lastSavedShiftsSignature = currentShiftsSig;
            }

            HasUnsavedChanges = false;
            OnPropertyChanged(nameof(UnsavedChangesText));

            _dialog.NotifySuccess("设置已保存");
            // 语言切换：保存到 settings.json，需重启后经 App 启动应用 CultureInfo 生效
            if (DraftSettings.Language != _lastSavedLanguage)
            {
                _lastSavedLanguage = DraftSettings.Language;
                _dialog.NotifyInfo(Resources.Strings.Common_RestartRequired);
            }
            SyncCollectorSettingsAsync(); // Remote 模式：采集参数同步到 Collector（热生效）
        }
        catch (Exception ex)
        {
            _dialog.NotifyError($"保存失败: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving;

    /// <summary>
    /// Remote 模式：把采集相关参数同步到 Collector（落 Collector 侧 settings.json 并热生效）。
    /// 解决"Remote 模式下设置改了采集进程无感知"的配置分裂——MainAPP 本地仍写自己的 settings.json
    /// （UI 设置），采集参数经 SignalR 推给 Collector。失败仅提示，不阻断保存。
    /// 全部用 GetService（可空）+ try/catch：测试宿主未注册 Remote 链路时静默跳过。
    /// </summary>
    private async void SyncCollectorSettingsAsync()
    {
        try
        {
            var runtimeMode = _services.GetService<IRuntimeMode>();
            if (runtimeMode is null || !runtimeMode.IsRemote) return;
            var client = _services.GetService<KanbanDataClient>();
            if (client is null || !client.IsConnected) return; // 未连接时本地保存仍生效，连接恢复后由用户再保存一次
            var dto = new CollectorSettingsDto
            {
                PollingIntervalMs = AppSettings.PollingIntervalMs,
                HistoryWriteIntervalScans = AppSettings.HistoryWriteIntervalScans,
                PlcBatchReadMaxLength = AppSettings.PlcBatchReadMaxLength,
                PlcBatchReadMaxGapSlots = AppSettings.PlcBatchReadMaxGapSlots,
                PlcBrand = (int)AppSettings.PlcConfig.Brand,
                PlcIpAddress = AppSettings.PlcConfig.IpAddress,
                PlcPort = AppSettings.PlcConfig.Port,
                PlcTimeoutMs = AppSettings.PlcConfig.TimeoutMs,
                Shifts = AppSettings.Shifts.Select(s => new ShiftConfigDto
                {
                    Name = s.Name,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                }).ToList(),
            };
            await client.SaveCollectorSettingsAsync(dto);
            _dialog?.NotifySuccess("采集服务参数已同步（轮询/班次/PLC 连接已对采集进程生效）");
        }
        catch (Exception ex)
        {
            _dialog?.NotifyWarning($"采集服务参数同步失败（本地已保存）：{ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelChanges()
    {
        ReplaceDraft(CloneSettings(AppSettings));
        _dialog.NotifyInfo("已取消未保存修改");
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var result = _dialog.Show("确定恢复设置默认值吗？当前未保存修改将被覆盖。", "恢复默认设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        ReplaceDraft(new AppSettings());
        _dialog.NotifyInfo("已恢复默认值，请点击保存设置后生效");
    }

    private void ReplaceDraft(AppSettings settings)
    {
        UnwireDraftEvents();
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
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(UnsavedChangesText));
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.PlcConfig = new PlcConfig
        {
            Brand = source.PlcConfig.Brand,
            IpAddress = source.PlcConfig.IpAddress,
            Port = source.PlcConfig.Port,
            TimeoutMs = source.PlcConfig.TimeoutMs,
            SiemensModel = source.PlcConfig.SiemensModel,
            SiemensRack = source.PlcConfig.SiemensRack,
            SiemensSlot = source.PlcConfig.SiemensSlot,
            ModbusUnitId = source.PlcConfig.ModbusUnitId,
            ModbusAddressStartWithZero = source.PlcConfig.ModbusAddressStartWithZero,
            ModbusRegisterFunction = source.PlcConfig.ModbusRegisterFunction,
            ModbusBitFunction = source.PlcConfig.ModbusBitFunction,
            ModbusDataFormat = source.PlcConfig.ModbusDataFormat,
            SiemensDataFormat = source.PlcConfig.SiemensDataFormat,
            SiemensBatchInt32Limit = source.PlcConfig.SiemensBatchInt32Limit,
            OmronReadSplits = source.PlcConfig.OmronReadSplits,
        };
        target.PollingIntervalMs = source.PollingIntervalMs;
        target.HistoryWriteIntervalScans = source.HistoryWriteIntervalScans;
        target.PlcBatchReadMaxLength = source.PlcBatchReadMaxLength;
        target.PlcBatchReadMaxGapSlots = source.PlcBatchReadMaxGapSlots;
        target.DashboardRefreshIntervalMs = source.DashboardRefreshIntervalMs;
        target.AppTitle = source.AppTitle;
        target.Language = source.Language;
        target.IsDarkTheme = source.IsDarkTheme;
        target.UiScale = source.UiScale;
        target.EnableAlarmSound = source.EnableAlarmSound;
        target.EnableAutomaticDailyReport = source.EnableAutomaticDailyReport;
        target.AutomaticDailyReportTime = source.AutomaticDailyReportTime;
        target.Shifts = new System.Collections.ObjectModel.ObservableCollection<ShiftConfig>(
            source.Shifts.Select(s => new ShiftConfig { Name = s.Name, StartTime = s.StartTime, EndTime = s.EndTime }));
    }

    /// <summary>
    /// 校验所有输入。返回 null 表示通过，否则返回错误消息。
    /// </summary>
    private string? Validate(AppSettings settings)
    {
        // 看板标题校验
        if (string.IsNullOrWhiteSpace(settings.AppTitle))
            return "看板标题不能为空";

        // IP 地址校验
        var ip = settings.PlcConfig.IpAddress;
        if (string.IsNullOrWhiteSpace(ip))
            return "IP 地址不能为空";
        if (!IPAddress.TryParse(ip, out _))
            return $"无效的 IP 地址: {ip}";

        // 端口校验
        var port = settings.PlcConfig.Port;
        if (port < 1 || port > 65535)
            return $"端口号必须在 1-65535 之间，当前值: {port}";

        if (!Enum.IsDefined(settings.PlcConfig.Brand))
            return $"不支持的 PLC 品牌: {settings.PlcConfig.Brand}";
        if (settings.PlcConfig.TimeoutMs < 100 || settings.PlcConfig.TimeoutMs > 60000)
            return $"PLC 超时必须在 100-60000 毫秒之间，当前值: {settings.PlcConfig.TimeoutMs}";
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && !SiemensModels.Contains(settings.PlcConfig.SiemensModel, StringComparer.OrdinalIgnoreCase))
            return $"不支持的 Siemens 型号: {settings.PlcConfig.SiemensModel}";
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensRack > 7)
            return $"Siemens Rack 必须在 0-7 之间，当前值: {settings.PlcConfig.SiemensRack}";
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensSlot > 31)
            return $"Siemens Slot 必须在 0-31 之间，当前值: {settings.PlcConfig.SiemensSlot}";
        if (settings.PlcConfig.Brand == PlcBrand.Siemens && settings.PlcConfig.SiemensBatchInt32Limit is < 1 or > 55)
            return $"Siemens 批量 Int32 上限必须在 1-55 之间，当前值: {settings.PlcConfig.SiemensBatchInt32Limit}";
        if (settings.PlcConfig.Brand == PlcBrand.Omron && settings.PlcConfig.OmronReadSplits is < 1 or > 999)
            return $"欧姆龙 FINS 读取切割长度必须在 1-999 之间，当前值: {settings.PlcConfig.OmronReadSplits}";
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusUnitId is < 1 or > 247)
            return $"Modbus UnitId 必须在 1-247 之间，当前值: {settings.PlcConfig.ModbusUnitId}";
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusRegisterFunction is not (3 or 4))
            return $"Modbus 寄存器功能码必须为 3 或 4，当前值: {settings.PlcConfig.ModbusRegisterFunction}";
        if (settings.PlcConfig.Brand == PlcBrand.ModbusTcp && settings.PlcConfig.ModbusBitFunction is not (1 or 2))
            return $"Modbus 位功能码必须为 1 或 2，当前值: {settings.PlcConfig.ModbusBitFunction}";
        if (!Enum.IsDefined(settings.PlcConfig.ModbusDataFormat) || !Enum.IsDefined(settings.PlcConfig.SiemensDataFormat))
            return "PLC 数据格式无效";

        // 轮询间隔校验
        var interval = settings.PollingIntervalMs;
        if (interval < 50)
            return $"轮询间隔不能小于 50ms，当前值: {interval}";

        // 历史写入间隔校验
        var historyInterval = settings.HistoryWriteIntervalScans;
        if (historyInterval < 1)
            return $"历史写入间隔不能小于 1，当前值: {historyInterval}";

        if (settings.PlcBatchReadMaxLength < 1 || settings.PlcBatchReadMaxLength > 1024)
            return $"PLC 批量读取数量必须在 1-1024 之间，当前值: {settings.PlcBatchReadMaxLength}";
        if (settings.PlcBatchReadMaxGapSlots < 0 || settings.PlcBatchReadMaxGapSlots > 16)
            return $"PLC 批量读取地址空洞必须在 0-16 之间，当前值: {settings.PlcBatchReadMaxGapSlots}";

        // 班次配置校验
        var shiftError = ShiftValidator.Validate(settings.Shifts);
        if (shiftError != null) return shiftError;

        return null;
    }

    private static PlcConfig ClonePlcConfig(PlcConfig source) => source.CreateSnapshot();

    private static string GetPlcConfigSignature(PlcConfig config) =>
        $"{config.Brand}|{config.IpAddress}|{config.Port}|{config.TimeoutMs}|{config.SiemensModel}|{config.SiemensRack}|{config.SiemensSlot}|{config.SiemensBatchInt32Limit}|{config.OmronReadSplits}|{config.ModbusUnitId}|{config.ModbusAddressStartWithZero}|{config.ModbusRegisterFunction}|{config.ModbusBitFunction}|{config.ModbusDataFormat}|{config.SiemensDataFormat}";
}
