using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using MainAPP.Resources;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Models;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace MainAPP.ViewModels;

internal static class RuntimeHealthText
{
    public static string Format(bool isConnected, bool isRunning, bool lastCycleSucceeded, int consecutiveFailures)
    {
        // 通信中断修复 2026-08-15：断开时必须返回 K419（通信中断），否则 UI 红色触发器永不命中
        if (!isConnected) return Strings.K419;
        if (!isRunning) return Strings.M014;
        return lastCycleSucceeded ? Strings.K418 : string.Format(Strings.F227, consecutiveFailures);
    }
}

public sealed partial class DeviceAcquisitionStatusItem : ObservableObject
{
    /// <summary>设备唯一标识，用作设备状态集合的增量同步键。
    /// 不得改用 DeviceName：设备重名会让同步字典抛 ArgumentException，
    /// 而同步发生在 1s 刷新定时器回调内，异常直通 Dispatcher 会终止进程。</summary>
    [ObservableProperty] private string _deviceId = string.Empty;
    [ObservableProperty] private string _deviceName = string.Empty;
    [ObservableProperty] private string _statusText = Strings.Status_Unknown;
    [ObservableProperty] private string _acquisitionText = Strings.K402;
    [ObservableProperty] private int _configuredAddressCount;
    [ObservableProperty] private int _okProduction;
    [ObservableProperty] private int _ngProduction;
    public string ReadSummary => ConfiguredAddressCount == 0 ? Strings.M161 : string.Format(Strings.F022, ConfiguredAddressCount);
    public string ProductionSummary => $"{OkProduction:N0} / {NgProduction:N0}";

    partial void OnConfiguredAddressCountChanged(int value) => OnPropertyChanged(nameof(ReadSummary));
    partial void OnOkProductionChanged(int value) => OnPropertyChanged(nameof(ProductionSummary));
    partial void OnNgProductionChanged(int value) => OnPropertyChanged(nameof(ProductionSummary));
}

/// <summary>
/// 运行状态监控页：只读展示 PLC 连接、采集循环和设备读取健康度。
/// </summary>
public partial class RuntimeMonitoringViewModel : ObservableObject, INavigationPageLifecycle, IDisposable
{
    private readonly IPlcConnectionManager _connectionManager;
    private readonly IPlcDataAcquisitionService _acquisitionService;
    private readonly AppSettings _appSettings;
    private readonly IRuntimeMode _runtimeMode;
    private readonly IDeviceRepository _deviceRepository;
    private readonly HistoryService _historyService;
    private readonly IPlcAddressCodecResolver? _addressCodecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly SystemResourceMonitor _systemResourceMonitor;
    private readonly IDialogService _dialogService;
    private readonly KanbanDataClient? _remoteClient;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>远程刷新版本守卫（RefreshFromRemote 防重入：过期响应丢弃）。</summary>
    private int _refreshVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    private bool _isConnected;
    [ObservableProperty] private bool _isAcquisitionRunning;
    [ObservableProperty] private string _connectionStatus = Strings.Conn_Disconnected;
    [ObservableProperty] private DateTime? _disconnectedAt;
    [ObservableProperty] private bool _isCollectorUnreachable;
    /// <summary>正在发起重连（用于禁用"重试连接"按钮，避免连点把 PLC 驱动建链请求打爆）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    private bool _isReconnecting;
    /// <summary>自动刷新已暂停。1Hz 刷新会让 P99 延迟、队列峰值、磁盘剩余这类准静态指标一直跳动，
    /// 用户既看不清数值也复制不下来；排查故障时"先暂停再读数"是刚性需求。</summary>
    [ObservableProperty] private bool _isAutoRefreshPaused;
    /// <summary>最近一次刷新本身的异常（区别于采集循环的 LastFailureMessage）。
    /// 刷新失败时数据停止更新，必须让用户看到，否则页面看上去"正常但不动"。</summary>
    [ObservableProperty] private string? _refreshErrorMessage;
    [ObservableProperty] private int _consecutiveFailures;
    [ObservableProperty] private int _totalDisconnectCount;
    [ObservableProperty] private int _completedCycles;
    [ObservableProperty] private int _failedCycles;
    [ObservableProperty] private bool _lastCycleSucceeded;
    [ObservableProperty] private int _consecutiveFailureCycles;
    [ObservableProperty] private DateTime? _lastFailureAt;
    [ObservableProperty] private string? _lastFailureMessage;
    [ObservableProperty] private long _lastCycleMilliseconds;
    [ObservableProperty] private double _averageCycleMilliseconds;
    [ObservableProperty] private long _maxCycleMilliseconds;
    [ObservableProperty] private long _cycleP95Milliseconds;
    [ObservableProperty] private long _cycleP99Milliseconds;
    [ObservableProperty] private int _lastSuccessfulDevices;
    [ObservableProperty] private int _configuredDevices;
    [ObservableProperty] private DateTime? _lastSuccessfulAt;
    [ObservableProperty] private DateTime _lastRefreshTime;
    [ObservableProperty] private int _successfulCycles;
    [ObservableProperty] private double _successRatePercent;
    [ObservableProperty] private int _estimatedReadOperations;
    [ObservableProperty] private int _pendingHistoryCount;
    [ObservableProperty] private bool _recoveryFileExists;
    [ObservableProperty] private long _recoveryFileBytes;
    [ObservableProperty] private DateTime? _lastHistoryFlushAt;
    [ObservableProperty] private int _historyFlushFailureCount;
    [ObservableProperty] private double _cpuUsagePercent;
    [ObservableProperty] private double _gpuUsagePercent;
    [ObservableProperty] private bool _gpuAvailable;
    [ObservableProperty] private double _memoryMb;
    [ObservableProperty] private TimeSpan _processUptime;
    [ObservableProperty] private int _configurationIssueCount;
    [ObservableProperty] private int _addressConflictCount;
    [ObservableProperty] private int _invalidAddressCount;
    [ObservableProperty] private int _configuredReadAddressCount;
    [ObservableProperty] private long _productionDatabaseBytes;
    [ObservableProperty] private long _productionWalBytes;
    [ObservableProperty] private int _pendingDataSourceCount;
    [ObservableProperty] private bool _dataSourceRecoveryFileExists;
    [ObservableProperty] private long _dataSourceRecoveryFileBytes;
    [ObservableProperty] private DateTime? _lastDataSourceFlushAt;
    [ObservableProperty] private int _dataSourceFlushFailureCount;
    [ObservableProperty] private long _productionQueuePeakCount;
    [ObservableProperty] private long _productionOverflowCount;
    [ObservableProperty] private long _dataSourceQueuePeakCount;
    [ObservableProperty] private long _dataSourceOverflowCount;
    [ObservableProperty] private long _productionFlushP95Milliseconds;
    [ObservableProperty] private long _productionFlushP99Milliseconds;
    [ObservableProperty] private long _dataSourceFlushP95Milliseconds;
    [ObservableProperty] private long _dataSourceFlushP99Milliseconds;
    [ObservableProperty] private long _dataSourceDatabaseBytes;
    [ObservableProperty] private long _dataSourceWalBytes;
    [ObservableProperty] private long _totalDatabaseBytes;
    [ObservableProperty] private long _totalWalBytes;
    [ObservableProperty] private int _batchPlanRebuilds;
    [ObservableProperty] private long _batchPlanBuildMilliseconds;
    [ObservableProperty] private long _dwordReadMilliseconds;
    [ObservableProperty] private long _alarmReadMilliseconds;
    [ObservableProperty] private long _defectReadMilliseconds;
    [ObservableProperty] private long _counterAlarmReadMilliseconds;
    [ObservableProperty] private long _historyWriteMilliseconds;
    [ObservableProperty] private double _availableMemoryMb;
    [ObservableProperty] private int _processThreadCount;
    [ObservableProperty] private long _processHandleCount;
    [ObservableProperty] private double _freeDiskGb;
    [ObservableProperty] private PlotModel _pollingTrend = CreatePollingTrendModel();
    private LineSeries _pollingTrendSeries = null!;

    public ObservableCollection<DeviceAcquisitionStatusItem> DeviceStatuses { get; } = new();

    public string PlcEndpoint => $"{_appSettings.PlcConfig.IpAddress}:{_appSettings.PlcConfig.Port}";
    public string DisconnectDurationText => DisconnectedAt is { } disconnectedAt
        ? FormatDuration(DateTime.Now - disconnectedAt)
        : Strings.M049;
    public string RecoveryFileText => RecoveryFileExists ? string.Format(Strings.F088, FormatBytes(RecoveryFileBytes)) : Strings.M052;
    public string DataConsistencyText => ConfigurationIssueCount == 0 ? Strings.M053 : string.Format(Strings.F078, ConfigurationIssueCount);
    public string DeviceReadSummary => $"{LastSuccessfulDevices} / {ConfiguredDevices}";
    public string CpuMemoryText => $"{CpuUsagePercent:F1}% / {MemoryMb:F0} MB";
    public string GpuUsageText => GpuAvailable ? $"{GpuUsagePercent:F1}%" : Strings.M054;
    public string AddressIssueSummary => $"{InvalidAddressCount} / {AddressConflictCount}";
    public string ProcessUptimeText => ProcessUptime.ToString(@"d\.hh\:mm\:ss");
    public string ReadDetailText => string.Format(Strings.F024, EstimatedReadOperations, ConfiguredReadAddressCount);
    public string HistoryStorageText => string.Format(Strings.F118, FormatBytes(ProductionDatabaseBytes), FormatBytes(ProductionWalBytes));
    public string DataSourceStorageText => string.Format(Strings.F118, FormatBytes(DataSourceDatabaseBytes), FormatBytes(DataSourceWalBytes));
    public string TotalStorageText => string.Format(Strings.F118, FormatBytes(TotalDatabaseBytes), FormatBytes(TotalWalBytes));
    public string DataSourceRecoveryFileText => DataSourceRecoveryFileExists ? string.Format(Strings.F088, FormatBytes(DataSourceRecoveryFileBytes)) : Strings.M052;
    public string HistoryQueueText => string.Format(
        Strings.Rtmon_HistoryQueueSummary,
        ProductionQueuePeakCount,
        ProductionOverflowCount,
        DataSourceQueuePeakCount,
        DataSourceOverflowCount);
    public string HistoryFlushLatencyText => string.Format(
        Strings.Rtmon_HistoryFlushSummary,
        ProductionFlushP95Milliseconds,
        ProductionFlushP99Milliseconds,
        HistoryFlushFailureCount,
        DataSourceFlushP95Milliseconds,
        DataSourceFlushP99Milliseconds,
        DataSourceFlushFailureCount);
    public string StageTimingText => string.Format(Strings.F002, DwordReadMilliseconds, AlarmReadMilliseconds, DefectReadMilliseconds, CounterAlarmReadMilliseconds, HistoryWriteMilliseconds);
    public string BatchPlanText => string.Format(Strings.F236, BatchPlanRebuilds, BatchPlanBuildMilliseconds);
    public string ProcessResourceText => string.Format(Strings.F186, ProcessThreadCount, ProcessHandleCount);
    public string SystemMemoryText => string.Format(Strings.F221, MemoryMb, AvailableMemoryMb);
    public string DiskFreeText => string.Format(Strings.F074, FreeDiskGb);
    public int PollingIntervalMs => _appSettings.PollingIntervalMs;
    public int HistoryWriteIntervalScans => _appSettings.HistoryWriteIntervalScans;
    public int TotalDeviceCount => _deviceRepository.GetDevicesSnapshot().Count;
    public string HealthText => RuntimeHealthText.Format(IsConnected, IsAcquisitionRunning, LastCycleSucceeded, ConsecutiveFailureCycles);
    public bool HasRefreshError => !string.IsNullOrWhiteSpace(RefreshErrorMessage);
    public bool HasActiveFailure => !IsConnected
        || HasRefreshError
        || (!LastCycleSucceeded && !string.IsNullOrWhiteSpace(LastFailureMessage));
    /// <summary>是否已有轮询趋势数据（用于空状态提示）。</summary>
    public bool HasPollingTrendData => _pollingTrendPoints.Count > 0;
    /// <summary>本地采集模式。只有本地模式才由本页直接发起 PLC 重连；
    /// Remote 模式的 SignalR 连接归上层 Coordinator 统一重连（含回调重新注册），
    /// 本页越权 ConnectAsync 会建出一条无回调注册的连接，页面永远收不到数据。</summary>
    public bool IsLocalMode => !_runtimeMode.IsRemote;
    /// <summary>"重试连接"可执行条件：连接归本进程管理、当前已断线、且未在重连中。</summary>
    public bool CanReconnectNow => IsLocalMode && !IsReconnecting && !IsConnected;
    /// <summary>暂停/继续按钮文案。</summary>
    public string AutoRefreshToggleText => IsAutoRefreshPaused
        ? Strings.Rtmon_ResumeAutoRefresh
        : Strings.Rtmon_PauseAutoRefresh;
    /// <summary>成功率显示值：尚无采集周期时返回 null（UI 显示"暂无"）。
    /// 直接给 0 会让刚启动时显示"0.0%"+空进度条，视觉含义等同"全部失败"，与"还没数据"是两回事。</summary>
    public double? SuccessRateDisplay => CompletedCycles == 0 ? null : SuccessRatePercent;

    public RuntimeMonitoringViewModel(
        IPlcConnectionManager connectionManager,
        IPlcDataAcquisitionService acquisitionService,
        AppSettings appSettings,
        IDeviceRepository deviceRepository,
        HistoryService historyService,
        SystemResourceMonitor systemResourceMonitor,
        IDialogService dialogService,
        IPlcAddressCodecResolver? addressCodecResolver = null,
        IPlcRuntimeProfileProvider? profileProvider = null,
        KanbanDataClient? remoteClient = null,
        IRuntimeMode? runtimeMode = null)
    {
        _connectionManager = connectionManager;
        _acquisitionService = acquisitionService;
        _appSettings = appSettings;
        _deviceRepository = deviceRepository;
        _historyService = historyService;
        _systemResourceMonitor = systemResourceMonitor;
        _dialogService = dialogService;
        _addressCodecResolver = addressCodecResolver;
        _profileProvider = profileProvider;
        _remoteClient = remoteClient;
        _runtimeMode = runtimeMode ?? new RuntimeMode(appSettings);
        _pollingTrendSeries = (LineSeries)_pollingTrend.Series[0];
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    public void OnPageEnter()
    {
        if (_refreshTimer.IsEnabled) return;
        RaiseConfigMetricsChanged();
        _refreshTimer.Start();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_refreshTimer.IsEnabled) return;
            Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void OnPageExit() => _refreshTimer.Stop();

    /// <summary>
    /// 配置类指标（PLC 地址 / 轮询间隔 / 历史写入间隔）只在进入页面时通知一次。
    /// 它们取自 AppSettings，运行期间不变；原先每秒 OnPropertyChanged 只是制造无效绑定重算，
    /// 还容易让人误以为这些配置是"实时采集到的运行状态"。
    /// </summary>
    private void RaiseConfigMetricsChanged()
    {
        OnPropertyChanged(nameof(PlcEndpoint));
        OnPropertyChanged(nameof(PollingIntervalMs));
        OnPropertyChanged(nameof(HistoryWriteIntervalScans));
    }

    /// <summary>暂停/继续自动刷新。恢复时立刻补一帧，不用干等下一个 tick。</summary>
    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        IsAutoRefreshPaused = !IsAutoRefreshPaused;
        if (!IsAutoRefreshPaused) Refresh();
    }

    partial void OnIsAutoRefreshPausedChanged(bool value)
        => OnPropertyChanged(nameof(AutoRefreshToggleText));

    [RelayCommand]
    private void Refresh()
    {
        // Remote 模式：采集/历史诊断在 Collector 进程，从 SignalR 拉取
        if (_remoteClient is not null && _runtimeMode.IsRemote)
        {
            RefreshFromRemoteAsync().Forget();
            return;
        }

        RefreshFromLocal();
    }

    /// <summary>
    /// 真正发起一次 PLC 重连（对应标题栏"重试连接"按钮）。
    /// 原实现把该按钮绑到 RefreshCommand——点了只是刷新显示，不会重连，属误导性按钮。
    /// EnsureConnected 内部会执行驱动建链（最长一个连接超时），必须在后台线程跑，
    /// 直接在 UI 线程调用会把整个界面卡在建链上。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReconnectNow))]
    private async Task ReconnectAsync()
    {
        IsReconnecting = true;
        try
        {
            await Task.Run(() => _connectionManager.EnsureConnected());
        }
        catch (Exception ex)
        {
            // EnsureConnected 内部已吞掉常规异常，这里兜住不可恢复异常（OOM 等）后如实呈现
            LastFailureMessage = ex.Message;
            LastFailureAt = DateTime.Now;
        }
        finally
        {
            IsReconnecting = false;
            Refresh(); // 立即把新的连接状态刷到界面，最多等 1s 定时器太慢
        }
    }

    /// <summary>
    /// 导出当前诊断快照到 .txt（UTF-8 带 BOM，记事本/工单系统直接可读）。
    /// 排查故障时只靠截图会丢失数值精度、也无法检索，落盘一份结构化文本是刚性需求。
    /// </summary>
    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        var path = _dialogService.ShowSaveFileDialog(
            Strings.Rtmon_ExportDiagnostics,
            // 文件名保持 ASCII：诊断快照常要贴到工单/邮件里外发，中文名在跨系统流转时易被改写。
            $"runtime_diagnostics_{DateTime.Now:yyyyMMddHHmm}.txt",
            Strings.Rtmon_TxtFilter);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var report = BuildDiagnosticsReport();
            // P1-8 修复 2026-09-02：报告可能很大，写盘移出 UI 线程（对齐 OverviewViewModel:315 范式）
            await Task.Run(() => File.WriteAllText(path, report, new UTF8Encoding(true)));
            _dialogService.NotifySuccess(string.Format(Strings.Rtmon_DiagnosticsExported, Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _dialogService.NotifyError(string.Format(Strings.F090, ex.Message));
        }
    }

    /// <summary>把同一份诊断快照复制到剪贴板，便于直接粘到工单/群聊里。</summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            // 剪贴板被其他进程独占时 SetText 会抛 ExternalException；
            // 失败时引导用户改用"导出"落盘，而不是静默吞掉让用户以为复制成功了。
            System.Windows.Clipboard.SetText(BuildDiagnosticsReport());
            _dialogService.NotifySuccess(Strings.Rtmon_DiagnosticsCopied);
        }
        catch (Exception ex)
        {
            _dialogService.NotifyError(string.Format(Strings.Rtmon_CopyFailed, ex.Message));
        }
    }

    /// <summary>
    /// 生成诊断快照文本：导出文件与复制剪贴板共用同一份内容，避免两处格式漂移。
    /// 指标标签直接复用界面上的本地化键，导出结果与用户看到的界面对得上号。
    /// 读取的是当前内存快照，不触发重新采集——导出的就是用户此刻看到的那一帧。
    /// </summary>
    public string BuildDiagnosticsReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Strings.Rtmon_DiagnosticsTitle);
        sb.AppendLine($"{Strings.Rtmon_DiagnosticsGeneratedAt}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"{Strings.Rtmon_RuntimeMode}: {(IsLocalMode ? Strings.Settings_CollectorLocalMode : Strings.K487)}");
        sb.AppendLine(string.Format(Strings.F284, LastRefreshTime));

        AppendSection(sb, Strings.Rtmon_DiagOverview);
        sb.AppendLine($"  {Strings.K425}: {HealthText}");
        sb.AppendLine($"  {Strings.K099}: {ConnectionStatus}");
        sb.AppendLine($"  {Strings.K100}: {TotalDisconnectCount}");
        sb.AppendLine($"  {Strings.K442}: {DisconnectDurationText}");
        sb.AppendLine($"  {Strings.K424}: {ConsecutiveFailureCycles}");
        sb.AppendLine($"  {Strings.K423}: {(string.IsNullOrWhiteSpace(LastFailureMessage) ? Strings.K583 : LastFailureMessage)}");
        if (LastFailureAt is { } failedAt)
            sb.AppendLine($"    {string.Format(Strings.F279, failedAt)}");
        if (HasRefreshError)
            sb.AppendLine($"  {Strings.Rtmon_RefreshFailed}: {RefreshErrorMessage}");
        if (IsCollectorUnreachable)
            sb.AppendLine($"  {Strings.Rtmon_CollectorUnreachable}");

        AppendSection(sb, Strings.K427);
        sb.AppendLine($"  {Strings.K428}: {string.Format(Strings.F247, CompletedCycles)}");
        sb.AppendLine($"  {Strings.K429}: {AverageCycleMilliseconds:F1} ms");
        sb.AppendLine($"  {Strings.K430}: {MaxCycleMilliseconds} ms");
        sb.AppendLine($"  {Strings.K431}: {PollingIntervalMs} ms");
        sb.AppendLine($"  {Strings.K432}: {string.Format(Strings.F283, HistoryWriteIntervalScans)}");

        AppendSection(sb, Strings.K434);
        sb.AppendLine($"  {Strings.K435}: {TotalDeviceCount}");
        sb.AppendLine($"  {Strings.K436}: {LastSuccessfulDevices}");
        sb.AppendLine($"  {Strings.K437}: {LastSuccessfulAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? Strings.K595}");
        sb.AppendLine($"  {Strings.K446}: {ReadDetailText}");
        sb.AppendLine($"  {Strings.K447}: {DeviceReadSummary}");

        AppendSection(sb, Strings.K440);
        sb.AppendLine($"  {Strings.K441}: {PlcEndpoint}");
        sb.AppendLine($"  {Strings.K442}: {DisconnectDurationText}");
        sb.AppendLine($"  {Strings.K443}: {string.Format(Strings.F291, ConsecutiveFailures)}");

        AppendSection(sb, Strings.K444);
        sb.AppendLine($"  {Strings.K445}: {(SuccessRateDisplay is { } rate ? $"{rate:F1}%" : Strings.K595)}");
        sb.AppendLine($"  {Strings.Rtmon_P95Cycle}: {CycleP95Milliseconds} ms");
        sb.AppendLine($"  {Strings.Rtmon_P99Cycle}: {CycleP99Milliseconds} ms");

        AppendSection(sb, Strings.K450);
        sb.AppendLine($"  {Strings.K451}: {PendingHistoryCount}");
        sb.AppendLine($"  {Strings.K452}: {RecoveryFileText}");
        sb.AppendLine($"  {Strings.K453}: {LastHistoryFlushAt?.ToString("HH:mm:ss") ?? Strings.K595}");
        sb.AppendLine($"  {Strings.K454}: {HistoryFlushFailureCount}");
        sb.AppendLine($"  {Strings.K455}: {HistoryStorageText}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourcePending}: {PendingDataSourceCount}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourceRecovery}: {DataSourceRecoveryFileText}");
        sb.AppendLine($"  {Strings.Rtmon_QueuePeakOverflow}: {HistoryQueueText}");
        sb.AppendLine($"  {Strings.Rtmon_FlushLatency}: {HistoryFlushLatencyText}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourceDatabase}: {DataSourceStorageText}");
        sb.AppendLine($"  {Strings.Rtmon_TotalDatabase}: {TotalStorageText}");

        AppendSection(sb, Strings.K456);
        sb.AppendLine($"  {Strings.K457}: {CpuMemoryText}");
        sb.AppendLine($"  {Strings.K458}: {ProcessUptimeText}");
        sb.AppendLine($"  {Strings.K459}: {GpuUsageText}");
        sb.AppendLine($"  {Strings.K460}: {DataConsistencyText}");
        sb.AppendLine($"  {Strings.K461}: {ProcessResourceText}");
        sb.AppendLine($"  {Strings.K462}: {SystemMemoryText}");
        sb.AppendLine($"  {Strings.K463}: {DiskFreeText}");
        sb.AppendLine($"  {Strings.Rtmon_AddressConflict}: {AddressConflictCount}");
        sb.AppendLine($"  {Strings.Rtmon_InvalidAddress}: {InvalidAddressCount}");
        sb.AppendLine($"  {Strings.K465}: {StageTimingText}");
        sb.AppendLine($"  {Strings.K466}: {BatchPlanText}");

        AppendSection(sb, Strings.K467);
        if (DeviceStatuses.Count == 0)
        {
            sb.AppendLine($"  {Strings.K583}");
        }
        else
        {
            // 制表符分隔：直接粘进 Excel / 工单表格能自动分列
            sb.AppendLine($"  {Strings.K002}\t{Strings.K083}\t{Strings.K468}\t{Strings.K469}\t{Strings.Wo_OkNg}");
            foreach (var d in DeviceStatuses)
                sb.AppendLine($"  {d.DeviceName}\t{d.StatusText}\t{d.AcquisitionText}\t{d.ReadSummary}\t{d.ProductionSummary}");
        }

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"[{title}]");
    }

    /// <summary>本地采集模式：直接读本进程采集/历史诊断（原行为不变）。</summary>
    private void RefreshFromLocal()
    {
        try
        {
            RefreshFromLocalCore();
        }
        catch (Exception ex)
        {
            // 1s 定时器回调里未捕获的异常会直通 Dispatcher 终止进程：
            // 设备快照/配置校验/资源采样任一环节抛异常都不该让监控页把整个应用带崩。
            HandleRefreshException(ex);
        }
    }

    private void RefreshFromLocalCore()
    {
        RefreshErrorMessage = null; // 本次刷新成功跑完即清除上一次的刷新异常
        var snapshot = _acquisitionService.GetDiagnosticsSnapshot();
        IsConnected = _connectionManager.IsConnected;
        IsCollectorUnreachable = false;
        IsAcquisitionRunning = _acquisitionService.IsRunning;
        ConnectionStatus = _connectionManager.ConnectionStatus;
        DisconnectedAt = _connectionManager.DisconnectedAt;
        TotalDisconnectCount = _connectionManager.TotalDisconnectCount;
        ConsecutiveFailures = _connectionManager.ConsecutiveFailures;
        OnPropertyChanged(nameof(DisconnectDurationText));
        CompletedCycles = snapshot.CompletedCycles;
        FailedCycles = snapshot.FailedCycles;
        LastCycleSucceeded = snapshot.LastCycleSucceeded;
        ConsecutiveFailureCycles = snapshot.ConsecutiveFailureCycles;
        LastFailureAt = snapshot.LastFailureAt;
        LastFailureMessage = snapshot.LastFailureMessage;
        LastCycleMilliseconds = snapshot.LastCycleMilliseconds;
        AverageCycleMilliseconds = snapshot.AverageCycleMilliseconds;
        MaxCycleMilliseconds = snapshot.MaxCycleMilliseconds;
        CycleP95Milliseconds = snapshot.CycleP95Milliseconds;
        CycleP99Milliseconds = snapshot.CycleP99Milliseconds;
        LastSuccessfulDevices = snapshot.LastSuccessfulDevices;
        ConfiguredDevices = snapshot.ConfiguredDevices;
        LastSuccessfulAt = snapshot.LastSuccessfulAt;
        SuccessfulCycles = snapshot.SuccessfulCycles;
        SuccessRatePercent = snapshot.CompletedCycles == 0
            ? 0
            : snapshot.SuccessfulCycles * 100.0 / snapshot.CompletedCycles;
        EstimatedReadOperations = snapshot.EstimatedReadOperations;
        ConfiguredReadAddressCount = _deviceRepository.GetDevicesSnapshot()
            .SelectMany(DeviceConfigValidator.GetDeviceAddresses)
            .Count(address => !string.IsNullOrWhiteSpace(address));

        var historySnapshot = _historyService.GetDiagnosticsSnapshot();
        PendingHistoryCount = historySnapshot.PendingProductionCount;
        RecoveryFileExists = historySnapshot.RecoveryFileExists;
        RecoveryFileBytes = historySnapshot.RecoveryFileBytes;
        LastHistoryFlushAt = historySnapshot.LastFlushAt;
        HistoryFlushFailureCount = historySnapshot.FlushFailureCount;
        ProductionDatabaseBytes = historySnapshot.ProductionDatabaseBytes;
        ProductionWalBytes = historySnapshot.ProductionWalBytes;
        PendingDataSourceCount = historySnapshot.PendingDataSourceCount;
        DataSourceRecoveryFileExists = historySnapshot.DataSourceRecoveryFileExists;
        DataSourceRecoveryFileBytes = historySnapshot.DataSourceRecoveryFileBytes;
        LastDataSourceFlushAt = historySnapshot.LastDataSourceFlushAt;
        DataSourceFlushFailureCount = historySnapshot.DataSourceFlushFailureCount;
        ProductionQueuePeakCount = historySnapshot.ProductionQueuePeakCount;
        ProductionOverflowCount = historySnapshot.ProductionOverflowCount;
        DataSourceQueuePeakCount = historySnapshot.DataSourceQueuePeakCount;
        DataSourceOverflowCount = historySnapshot.DataSourceOverflowCount;
        ProductionFlushP95Milliseconds = historySnapshot.ProductionFlushP95Milliseconds;
        ProductionFlushP99Milliseconds = historySnapshot.ProductionFlushP99Milliseconds;
        DataSourceFlushP95Milliseconds = historySnapshot.DataSourceFlushP95Milliseconds;
        DataSourceFlushP99Milliseconds = historySnapshot.DataSourceFlushP99Milliseconds;
        DataSourceDatabaseBytes = historySnapshot.DataSourceDatabaseBytes;
        DataSourceWalBytes = historySnapshot.DataSourceWalBytes;
        TotalDatabaseBytes = historySnapshot.TotalDatabaseBytes;
        TotalWalBytes = historySnapshot.TotalWalBytes;
        BatchPlanRebuilds = snapshot.BatchPlanRebuilds;
        BatchPlanBuildMilliseconds = snapshot.BatchPlanBuildMilliseconds;
        DwordReadMilliseconds = snapshot.DWordReadMilliseconds;
        AlarmReadMilliseconds = snapshot.AlarmReadMilliseconds;
        DefectReadMilliseconds = snapshot.DefectReadMilliseconds;
        CounterAlarmReadMilliseconds = snapshot.CounterAlarmReadMilliseconds;
        HistoryWriteMilliseconds = snapshot.HistoryWriteMilliseconds;

        UpdateResourceMetrics();
        MaybeUpdateConsistencyMetrics();
        UpdateDeviceStatuses(snapshot.LastSuccessfulDeviceIds);
        UpdatePollingTrend();
        OnPropertyChanged(nameof(HasPollingTrendData));
        LastRefreshTime = DateTime.Now;
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HasActiveFailure));
        OnPropertyChanged(nameof(TotalDeviceCount));
        OnPropertyChanged(nameof(SuccessRateDisplay));
        OnPropertyChanged(nameof(RecoveryFileText));
        OnPropertyChanged(nameof(DataConsistencyText));
        OnPropertyChanged(nameof(DeviceReadSummary));
        OnPropertyChanged(nameof(CpuMemoryText));
        OnPropertyChanged(nameof(GpuUsageText));
        OnPropertyChanged(nameof(AddressIssueSummary));
        OnPropertyChanged(nameof(ProcessUptimeText));
        OnPropertyChanged(nameof(ReadDetailText));
        OnPropertyChanged(nameof(HistoryStorageText));
        OnPropertyChanged(nameof(DataSourceStorageText));
        OnPropertyChanged(nameof(TotalStorageText));
        OnPropertyChanged(nameof(DataSourceRecoveryFileText));
        OnPropertyChanged(nameof(HistoryQueueText));
        OnPropertyChanged(nameof(HistoryFlushLatencyText));
        OnPropertyChanged(nameof(StageTimingText));
        OnPropertyChanged(nameof(BatchPlanText));
        OnPropertyChanged(nameof(ProcessResourceText));
        OnPropertyChanged(nameof(SystemMemoryText));
        OnPropertyChanged(nameof(DiskFreeText));
    }

    /// <summary>Remote 模式：从 Collector 拉取诊断快照（异步，避免阻塞 UI 刷新）。</summary>
    /// <remarks>防重入：1s 定时器触发，若上一次拉取未完成（网络慢），版本守卫丢弃过期响应，
    /// 避免旧数据覆盖新数据导致数值回跳（对齐 OverviewViewModel 的 _refreshVersion 模式）。</remarks>
    private async Task RefreshFromRemoteAsync()
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        try
        {
            var d = await _remoteClient!.GetDiagnosticsAsync();
            if (refreshVersion != Volatile.Read(ref _refreshVersion)) return; // 已有更新的刷新，丢弃过期响应
            RefreshErrorMessage = null; // 拉取成功且响应未过期才清除上一次的刷新异常
            IsConnected = d.IsConnected;
            IsCollectorUnreachable = false;
            // 采集状态用真值（CollectorDiagnosticsDto.IsRunning）：连接正常 ≠ 采集运行中，
            // 原先用 IsConnected 会在"连接正常但采集停止"时误报"运行中"。
            IsAcquisitionRunning = d.IsRunning;
            ConnectionStatus = d.ConnectionStatus;
            DisconnectedAt = d.DisconnectedAt;
            TotalDisconnectCount = d.TotalDisconnectCount;
            ConsecutiveFailures = d.ConsecutiveFailures;
            OnPropertyChanged(nameof(DisconnectDurationText));
            CompletedCycles = d.CompletedCycles;
            FailedCycles = d.FailedCycles;
            LastCycleSucceeded = d.LastCycleSucceeded;
            ConsecutiveFailureCycles = d.ConsecutiveFailureCycles;
            LastFailureAt = d.LastFailureAt;
            LastFailureMessage = d.LastFailureMessage;
            LastCycleMilliseconds = d.LastCycleMilliseconds;
            AverageCycleMilliseconds = d.AverageCycleMilliseconds;
            MaxCycleMilliseconds = d.MaxCycleMilliseconds;
            CycleP95Milliseconds = d.CycleP95Milliseconds;
            CycleP99Milliseconds = d.CycleP99Milliseconds;
            LastSuccessfulDevices = d.LastSuccessfulDevices;
            ConfiguredDevices = d.ConfiguredDevices;
            LastSuccessfulAt = d.LastSuccessfulAt;
            SuccessfulCycles = d.SuccessfulCycles;
            SuccessRatePercent = d.CompletedCycles == 0
                ? 0
                : d.SuccessfulCycles * 100.0 / d.CompletedCycles;
            EstimatedReadOperations = d.EstimatedReadOperations;
            ConfiguredReadAddressCount = d.ConfiguredReadAddressCount;
            PendingHistoryCount = d.PendingHistoryCount;
            RecoveryFileExists = d.RecoveryFileExists;
            RecoveryFileBytes = d.RecoveryFileBytes;
            LastHistoryFlushAt = d.LastHistoryFlushAt;
            HistoryFlushFailureCount = d.HistoryFlushFailureCount;
            ProductionDatabaseBytes = d.ProductionDatabaseBytes;
            ProductionWalBytes = d.ProductionWalBytes;
            PendingDataSourceCount = d.PendingDataSourceCount;
            DataSourceRecoveryFileExists = d.DataSourceRecoveryFileExists;
            DataSourceRecoveryFileBytes = d.DataSourceRecoveryFileBytes;
            LastDataSourceFlushAt = d.LastDataSourceFlushAt;
            DataSourceFlushFailureCount = d.DataSourceFlushFailureCount;
            ProductionQueuePeakCount = d.ProductionQueuePeakCount;
            ProductionOverflowCount = d.ProductionOverflowCount;
            DataSourceQueuePeakCount = d.DataSourceQueuePeakCount;
            DataSourceOverflowCount = d.DataSourceOverflowCount;
            ProductionFlushP95Milliseconds = d.ProductionFlushP95Milliseconds;
            ProductionFlushP99Milliseconds = d.ProductionFlushP99Milliseconds;
            DataSourceFlushP95Milliseconds = d.DataSourceFlushP95Milliseconds;
            DataSourceFlushP99Milliseconds = d.DataSourceFlushP99Milliseconds;
            DataSourceDatabaseBytes = d.DataSourceDatabaseBytes;
            DataSourceWalBytes = d.DataSourceWalBytes;
            TotalDatabaseBytes = d.TotalDatabaseBytes;
            TotalWalBytes = d.TotalWalBytes;
            BatchPlanRebuilds = d.BatchPlanRebuilds;
            BatchPlanBuildMilliseconds = d.BatchPlanBuildMilliseconds;
            DwordReadMilliseconds = d.DWordReadMilliseconds;
            AlarmReadMilliseconds = d.AlarmReadMilliseconds;
            DefectReadMilliseconds = d.DefectReadMilliseconds;
            CounterAlarmReadMilliseconds = d.CounterAlarmReadMilliseconds;
            HistoryWriteMilliseconds = d.HistoryWriteMilliseconds;

            UpdateResourceMetrics();
            MaybeUpdateConsistencyMetrics();
            // Remote 模式也刷新周期趋势；设备明细列表待诊断 DTO 补充成功后同步（审查修复 2026-08-15 第一步）
            UpdatePollingTrend();
            UpdateDeviceStatusesFromRemote(d.DeviceStatuses);
            OnPropertyChanged(nameof(HasPollingTrendData));
            LastRefreshTime = DateTime.Now;
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(HasActiveFailure));
            OnPropertyChanged(nameof(TotalDeviceCount));
            OnPropertyChanged(nameof(SuccessRateDisplay));
            OnPropertyChanged(nameof(RecoveryFileText));
            OnPropertyChanged(nameof(DataConsistencyText));
            OnPropertyChanged(nameof(DeviceReadSummary));
            OnPropertyChanged(nameof(CpuMemoryText));
            OnPropertyChanged(nameof(GpuUsageText));
            OnPropertyChanged(nameof(AddressIssueSummary));
            OnPropertyChanged(nameof(ProcessUptimeText));
            OnPropertyChanged(nameof(ReadDetailText));
            OnPropertyChanged(nameof(HistoryStorageText));
            OnPropertyChanged(nameof(DataSourceStorageText));
            OnPropertyChanged(nameof(TotalStorageText));
            OnPropertyChanged(nameof(DataSourceRecoveryFileText));
            OnPropertyChanged(nameof(HistoryQueueText));
            OnPropertyChanged(nameof(HistoryFlushLatencyText));
            OnPropertyChanged(nameof(StageTimingText));
            OnPropertyChanged(nameof(BatchPlanText));
            OnPropertyChanged(nameof(ProcessResourceText));
            OnPropertyChanged(nameof(SystemMemoryText));
            OnPropertyChanged(nameof(DiskFreeText));
        }
        catch (Exception ex)
        {
            // Collector 未连接：显示离线状态，不崩溃。旧请求失败不覆盖在途的新请求（版本守卫）。
            if (refreshVersion != Volatile.Read(ref _refreshVersion)) return;
            IsConnected = false;
            IsCollectorUnreachable = true;
            IsAcquisitionRunning = false;
            ConnectionStatus = Strings.M050;
            DisconnectedAt = DateTime.Now;
            LastFailureMessage = ex.Message;
            LastFailureAt = DateTime.Now;
            OnPropertyChanged(nameof(DisconnectDurationText));
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(HasActiveFailure));
            OnPropertyChanged(nameof(SuccessRateDisplay));
        }
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (IsAutoRefreshPaused) return; // 暂停读数：定时器继续跑但不出帧，恢复时无需重建

        // 最后一道兜底：Refresh 内部已各自保护，这里防止任何遗漏的异常冒泡到 Dispatcher
        // 变成未处理异常终止进程（1s 一次，异常不处理就是每秒一次崩溃）。
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            HandleRefreshException(ex);
        }
    }

    /// <summary>
    /// 刷新异常统一处理：呈现到告警条而不是冒泡终止进程。
    /// 注意不更新 LastRefreshTime——刷新失败时时间戳停在旧值，用户能看出数据已停滞。
    /// Remote 分支的失败不进这里：那走 IsCollectorUnreachable 单独呈现（采集服务不可达 ≠ 刷新异常）。
    /// </summary>
    private void HandleRefreshException(Exception ex)
    {
        RefreshErrorMessage = ex.Message; // HasRefreshError / HasActiveFailure 由 OnRefreshErrorMessageChanged 联动通知
    }

    partial void OnRefreshErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRefreshError));
        OnPropertyChanged(nameof(HasActiveFailure));
    }

    private void UpdateResourceMetrics()
    {
        try
        {
            var snapshot = _systemResourceMonitor.Sample();
            CpuUsagePercent = snapshot.CpuUsagePercent;
            GpuUsagePercent = snapshot.GpuUsagePercent;
            GpuAvailable = snapshot.GpuAvailable;
            MemoryMb = snapshot.ProcessMemoryMb;
            ProcessUptime = snapshot.ProcessUptime;
            ProcessThreadCount = snapshot.ThreadCount;
            ProcessHandleCount = snapshot.HandleCount;
            AvailableMemoryMb = snapshot.AvailableMemoryMb;
            FreeDiskGb = snapshot.FreeDiskGb;
        }
        catch
        {
            CpuUsagePercent = 0;
            MemoryMb = 0;
            AvailableMemoryMb = 0;
            FreeDiskGb = 0;
        }
    }

    private void UpdateDeviceStatusesFromRemote(IReadOnlyList<CollectorDeviceStatusDto> devices)
    {
        var desired = new List<DeviceAcquisitionStatusItem>(devices.Count);
        foreach (var d in devices)
        {
            desired.Add(new DeviceAcquisitionStatusItem
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                StatusText = RuntimeDeviceStatusText.Format(d.StatusWord),
                AcquisitionText = d.ConfiguredAddressCount == 0
                    ? Strings.M162
                    : d.LastCycleSucceeded ? Strings.M163 : Strings.M164,
                ConfiguredAddressCount = d.ConfiguredAddressCount,
                OkProduction = d.OkProduction,
                NgProduction = d.NgProduction,
            });
        }
        DeviceStatusCollectionSynchronizer.Synchronize(DeviceStatuses, desired);
    }

    private void UpdateDeviceStatuses(IReadOnlySet<string> lastSuccessfulDeviceIds)
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        var desired = new List<DeviceAcquisitionStatusItem>(devices.Count);
        foreach (var device in devices)
        {
            _deviceRepository.RuntimeMap.TryGetValue(device.Id, out var runtime);
            var addresses = DeviceConfigValidator.GetDeviceAddresses(device)
                .Count(address => !string.IsNullOrWhiteSpace(address));
            desired.Add(new DeviceAcquisitionStatusItem
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                StatusText = GetStatusText(runtime?.StatusWord ?? 0),
                AcquisitionText = addresses == 0 ? Strings.M162 : lastSuccessfulDeviceIds.Contains(device.Id) ? Strings.M163 : Strings.M164,
                ConfiguredAddressCount = addresses,
                OkProduction = runtime?.OkProduction ?? 0,
                NgProduction = runtime?.NgProduction ?? 0,
            });
        }
        DeviceStatusCollectionSynchronizer.Synchronize(DeviceStatuses, desired);
    }

    private void UpdatePollingTrend()
    {
        var now = DateTime.Now;
        var points = _pollingTrendPoints.Add(now, LastCycleMilliseconds);
        _pollingTrendSeries.Points.Clear();
        _pollingTrendSeries.Points.AddRange(points);
        PollingTrend.InvalidatePlot(false);
    }

    private static PlotModel CreatePollingTrendModel()
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBorderColor = ChartPalette.Axis,
            PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1),
        };
        model.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm:ss", TextColor = ChartPalette.MutedText, AxislineColor = OxyColors.Transparent, MajorGridlineStyle = LineStyle.None });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Title = Strings.M357, TextColor = ChartPalette.MutedText, AxislineColor = OxyColors.Transparent, MajorGridlineColor = ChartPalette.Grid, MajorGridlineStyle = LineStyle.Solid });
        model.Series.Add(new LineSeries { Color = ChartPalette.Base, StrokeThickness = 2, MarkerType = MarkerType.None });
        return model;
    }

    private static string GetStatusText(int statusWord) => RuntimeDeviceStatusText.Format(statusWord);

    /// <summary>
    /// 一致性校验降频（每 10 次 tick ≈ 10s 执行一次）：CollectValidationErrors + CollectCrossDeviceConflicts
    /// + 逐地址 codec.Parse 在设备/地址多时开销大，每秒执行会卡 UI。
    /// 采集状态/资源指标保持每秒刷新；进入页面首帧立即执行一次（_consistencyTickCount 初始为阈值前值）。
    /// 注：CollectValidationErrors 内部已含一次冲突检测，此处再单独统计属现状（降频后成本可接受）。
    /// </summary>
    private const int ConsistencyCheckIntervalTicks = 10;
    private int _consistencyTickCount = ConsistencyCheckIntervalTicks - 1;

    private void MaybeUpdateConsistencyMetrics()
    {
        if (++_consistencyTickCount < ConsistencyCheckIntervalTicks) return;
        _consistencyTickCount = 0;
        UpdateConsistencyMetrics();
    }

    private void UpdateConsistencyMetrics()
    {
        var devices = _deviceRepository.GetDevicesSnapshot();
        var errors = DeviceConfigValidator.CollectValidationErrors(
            devices,
            _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current);
        ConfigurationIssueCount = errors.Count;
        AddressConflictCount = DeviceConfigValidator.CollectCrossDeviceConflicts(
            devices,
            _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current).Count;
        var codec = _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current ?? new MitsubishiAddressCodec();
        InvalidAddressCount = devices.SelectMany(DeviceConfigValidator.GetDeviceAddresses).Count(address =>
            !string.IsNullOrWhiteSpace(address) && !codec.Parse(address).IsValid);
    }

    private static string FormatDuration(TimeSpan duration)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatStandard(duration.TotalSeconds);

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1}{Strings.M358}"
        : $"{bytes / 1024d:F1}{Strings.M359}";

    public void Dispose()
    {
        OnPageExit();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _pollingTrendPoints.Clear();
        // 注意：不释放 _systemResourceMonitor——它是 DI 容器持有的单例（级联单例 GpuUsageMonitor），
        // 生命周期归容器管，由页面 VM 释放属所有权违规（host.Dispose 统一释放）。
    }

    private readonly PollingTrendBuffer _pollingTrendPoints = new();
}
