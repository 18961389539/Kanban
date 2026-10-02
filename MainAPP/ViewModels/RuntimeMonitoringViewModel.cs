using System.Windows.Threading;
using Kanban.Collector.Core.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using MainAPP.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Data;
using MainAPP.Models;
using Kanban.Collector.Core.Services;
using MainAPP.Services;
using MainAPP.Helpers;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace MainAPP.ViewModels;

internal static class RuntimeHealthText
{
    public static string Format(bool isConnected, bool isRunning, bool lastCycleSucceeded, int consecutiveFailures)
    {
        // 通信中断修复 2026-08-15：断开时必须返回 K419（通信中断），否则 UI 红色触发器永不命中
        if (!isConnected) return Strings.Lbl_CommunicationLost;
        if (!isRunning) return Strings.Msg_AcquisitionStopped;
        return lastCycleSucceeded ? Strings.Lbl_RunningNormally : string.Format(Strings.Prompt_ConsecutiveFailures, consecutiveFailures);
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
    [ObservableProperty] private string _acquisitionText = Strings.Lbl_DeviceRunningNoOutputAcquired;
    [ObservableProperty] private int _configuredAddressCount;
    [ObservableProperty] private int _okProduction;
    [ObservableProperty] private int _ngProduction;
    public string ReadSummary => ConfiguredAddressCount == 0 ? Strings.Msg_NoAddressConfigured : string.Format(Strings.Prompt_Addresses, ConfiguredAddressCount);
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
    private readonly IHistoryService _historyService;
    private readonly IPlcAddressCodecResolver? _addressCodecResolver;
    private readonly IPlcRuntimeProfileProvider? _profileProvider;
    private readonly SystemResourceMonitor _systemResourceMonitor;
    private readonly IDialogService _dialogService;
    private readonly KanbanDataClient? _remoteClient;
    private readonly PageRefreshTimer _refreshTimer;

    /// <summary>刷新进行中。上一轮未完成时不再发新请求，只记一次“结束后再刷”。</summary>
    private int _refreshActive;
    private int _refreshAgain;
    private int _disposed;
    private readonly CollectorOutageClock _collectorOutage = new();
    /// <summary>当前要显示的断线秒数。来自采集进程快照，或采集服务不可达后的本地计时。</summary>
    private double? _disconnectDurationSeconds;
    private DateTime _nextConsistencyUtc = DateTime.MinValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    [NotifyPropertyChangedFor(nameof(AcquisitionHealthTooltip))]
    private bool _isConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    [NotifyPropertyChangedFor(nameof(AcquisitionHealthTooltip))]
    private bool _isAcquisitionRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    [NotifyPropertyChangedFor(nameof(AcquisitionHealthTooltip))]
    private string _connectionStatus = Strings.Conn_Disconnected;
    [ObservableProperty]
    private DateTime? _disconnectedAt;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    [NotifyPropertyChangedFor(nameof(AcquisitionHealthTooltip))]
    private bool _lastCycleSucceeded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsecutiveFailTooltip))]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    [NotifyPropertyChangedFor(nameof(AcquisitionHealthTooltip))]
    private int _consecutiveFailureCycles;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingHistoryTooltip))]
    private int _pendingHistoryCount;
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
    public string DisconnectDurationText => _disconnectDurationSeconds is { } seconds
        ? FormatDuration(TimeSpan.FromSeconds(seconds))
        : Strings.Msg_Disconnected;
    public string ConnectionTooltip => FormatHelper.Tip(Strings.Mo_Tip_ConnStatus, ConnectionStatus);
    public string AcquisitionHealthTooltip => FormatHelper.Tip(Strings.Mo_Tip_Health, HealthText);
    public string DisconnectDurationTooltip => FormatHelper.Tip(
        Strings.Mo_Tip_DisconnectDuration,
        string.IsNullOrEmpty(DisconnectDurationText) ? "—" : DisconnectDurationText);
    public string RecoveryFileText => RecoveryFileExists ? string.Format(Strings.Prompt_Present, FormatBytes(RecoveryFileBytes)) : Strings.Msg_NoBacklog;
    public string DataConsistencyText => ConfigurationIssueCount == 0 ? Strings.Msg_ConfigurationOK : string.Format(Strings.Prompt_IssuesFound, ConfigurationIssueCount);
    public string DeviceReadSummary => $"{LastSuccessfulDevices} / {ConfiguredDevices}";
    public string CpuMemoryText => $"{CpuUsagePercent:F1}% / {MemoryMb:F0} MB";
    public string GpuUsageText => GpuAvailable ? $"{GpuUsagePercent:F1}%" : Strings.Msg_Unavailable;
    public string AddressIssueSummary => $"{InvalidAddressCount} / {AddressConflictCount}";
    public string ProcessUptimeText => ProcessUptime.ToString(@"d\.hh\:mm\:ss");
    public string ReadDetailText => string.Format(Strings.Prompt_Addresses2, EstimatedReadOperations, ConfiguredReadAddressCount);
    public string HistoryStorageText => string.Format(Strings.Prompt_DBWAL, FormatBytes(ProductionDatabaseBytes), FormatBytes(ProductionWalBytes));
    public string DataSourceStorageText => string.Format(Strings.Prompt_DBWAL, FormatBytes(DataSourceDatabaseBytes), FormatBytes(DataSourceWalBytes));
    public string TotalStorageText => string.Format(Strings.Prompt_DBWAL, FormatBytes(TotalDatabaseBytes), FormatBytes(TotalWalBytes));
    public string DataSourceRecoveryFileText => DataSourceRecoveryFileExists ? string.Format(Strings.Prompt_Present, FormatBytes(DataSourceRecoveryFileBytes)) : Strings.Msg_NoBacklog;
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
    public string StageTimingText => string.Format(Strings.Prompt_DWordMsMBitMsDefect, DwordReadMilliseconds, AlarmReadMilliseconds, DefectReadMilliseconds, CounterAlarmReadMilliseconds, HistoryWriteMilliseconds);
    public string BatchPlanText => string.Format(Strings.Prompt_RebuildsLastMs, BatchPlanRebuilds, BatchPlanBuildMilliseconds);
    public string ProcessResourceText => string.Format(Strings.Prompt_ThreadsHandles, ProcessThreadCount, ProcessHandleCount);
    public string SystemMemoryText => string.Format(Strings.Prompt_ProcessMBAvailableMB, MemoryMb, AvailableMemoryMb);
    public string DiskFreeText => string.Format(Strings.Prompt_GBFree, FreeDiskGb);
    public int PollingIntervalMs => _appSettings.PollingIntervalMs;
    public int HistoryWriteIntervalScans => _appSettings.HistoryWriteIntervalScans;
    public int TotalDeviceCount => DeviceStatuses.Count;
    public string HealthText => RuntimeHealthText.Format(IsConnected, IsAcquisitionRunning, LastCycleSucceeded, ConsecutiveFailureCycles);
    public string SuccessRateTooltip => FormatHelper.Tip(
        Strings.Mo_Tip_SuccessRate,
        SuccessRateDisplay is { } rate ? $"{rate:F1}%" : Strings.Lbl_N);
    public string ConsecutiveFailTooltip => FormatHelper.Tip(Strings.Mo_Tip_ConsecutiveFail, ConsecutiveFailureCycles);
    public string PendingHistoryTooltip => FormatHelper.Tip(Strings.Mo_Tip_PendingHistory, PendingHistoryCount);
    public bool HasRefreshError => !string.IsNullOrWhiteSpace(RefreshErrorMessage);
    public bool HasActiveFailure => !IsConnected
        || HasRefreshError
        || (!LastCycleSucceeded && !string.IsNullOrWhiteSpace(LastFailureMessage));
    /// <summary>是否已有轮询趋势数据（用于空状态提示）。点来自采集周期，不是页面刷新。</summary>
    public bool HasPollingTrendData => _pollingTrendSeries.Points.Count > 0;
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
        IHistoryService historyService,
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
        _refreshTimer = new PageRefreshTimer(TimeSpan.FromSeconds(1), OnRefreshTimerTick);
    }

    public void OnPageEnter()
    {
        if (_refreshTimer.IsEnabled) return;
        RaiseConfigMetricsChanged();
        _refreshTimer.Start();
        UiDispatcher.PostOrDrop(() =>
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
        if (Interlocked.CompareExchange(ref _refreshActive, 1, 0) != 0)
        {
            // 上一轮还没结束：不再发新请求，只要求它结束后补一帧。
            Interlocked.Exchange(ref _refreshAgain, 1);
            return;
        }

        RunRefreshLoopAsync().Forget();
    }

    /// <summary>
    /// 同一时刻只跑一轮刷新。定时器 1 秒一次，但诊断采样可能超过 1 秒；
    /// 上一轮没回来就再发，只会把 Collector 或本机采样越堆越慢。
    /// </summary>
    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0) break;
                Interlocked.Exchange(ref _refreshAgain, 0);
                try
                {
                    await RefreshOnceAsync();
                }
                catch (Exception ex)
                {
                    HandleRefreshException(ex);
                }

                if (Volatile.Read(ref _disposed) != 0) break;
                if (Interlocked.Exchange(ref _refreshAgain, 0) == 0)
                    break;
            }
        }
        finally
        {
            Volatile.Write(ref _refreshActive, 0);
            if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _refreshAgain, 0) == 1)
                Refresh();
        }
    }

    private async Task RefreshOnceAsync()
    {
        if (_remoteClient is not null && _runtimeMode.IsRemote)
            await RefreshFromRemoteAsync();
        else
            await RefreshFromLocalAsync();
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
            _dialogService.NotifyError(string.Format(Strings.Prompt_ExportFailed, ex.Message));
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
        sb.AppendLine($"{Strings.Rtmon_RuntimeMode}: {(IsLocalMode ? Strings.Settings_CollectorLocalMode : Strings.Lbl_RemoteAcquisitionMode)}");
        sb.AppendLine(string.Format(Strings.Prompt_Refreshed, LastRefreshTime));

        AppendSection(sb, Strings.Rtmon_DiagOverview);
        sb.AppendLine($"  {Strings.Lbl_SystemHealth}: {HealthText}");
        sb.AppendLine($"  {Strings.Lbl_PLCConnection}: {ConnectionStatus}");
        sb.AppendLine($"  {Strings.Lbl_CumulativeDisconnects}: {TotalDisconnectCount}");
        sb.AppendLine($"  {Strings.Lbl_DisconnectDuration}: {DisconnectDurationText}");
        sb.AppendLine($"  {Strings.Lbl_ConsecutiveFailures}: {ConsecutiveFailureCycles}");
        sb.AppendLine($"  {Strings.Lbl_AcquisitionAbnormal}: {(string.IsNullOrWhiteSpace(LastFailureMessage) ? Strings.Lbl_NoData : LastFailureMessage)}");
        if (LastFailureAt is { } failedAt)
            sb.AppendLine($"    {string.Format(Strings.Prompt_LastOccurred, failedAt)}");
        if (HasRefreshError)
            sb.AppendLine($"  {Strings.Rtmon_RefreshFailed}: {RefreshErrorMessage}");
        if (IsCollectorUnreachable)
            sb.AppendLine($"  {Strings.Rtmon_CollectorUnreachable}");

        AppendSection(sb, Strings.Lbl_AcquisitionLoop);
        sb.AppendLine($"  {Strings.Lbl_CompletedPolls}: {string.Format(Strings.Prompt_Times3, CompletedCycles)}");
        sb.AppendLine($"  {Strings.Lbl_AverageCycle}: {AverageCycleMilliseconds:F1} ms");
        sb.AppendLine($"  {Strings.Lbl_MaxCycle}: {MaxCycleMilliseconds} ms");
        sb.AppendLine($"  {Strings.Lbl_ConfiguredPollInterval}: {PollingIntervalMs} ms");
        sb.AppendLine($"  {Strings.Lbl_HistoryWriteFrequency}: {string.Format(Strings.Prompt_EveryPolls, HistoryWriteIntervalScans)}");

        AppendSection(sb, Strings.Lbl_DeviceReads);
        sb.AppendLine($"  {Strings.Lbl_ConfiguredDevices}: {TotalDeviceCount}");
        sb.AppendLine($"  {Strings.Lbl_RecentSuccessfulReads}: {LastSuccessfulDevices}");
        sb.AppendLine($"  {Strings.Lbl_LastSuccessTime}: {LastSuccessfulAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? Strings.Lbl_N}");
        sb.AppendLine($"  {Strings.Lbl_ReadPointsConfigured}: {ReadDetailText}");
        sb.AppendLine($"  {Strings.Lbl_RecentSuccessfulDevices}: {DeviceReadSummary}");

        AppendSection(sb, Strings.Lbl_PLCCommunicationDetails);
        sb.AppendLine($"  {Strings.Lbl_ConnectionEndpoint}: {PlcEndpoint}");
        sb.AppendLine($"  {Strings.Lbl_ConsecutiveFailureRetries}: {string.Format(Strings.Prompt_Times4, ConsecutiveFailures)}");

        AppendSection(sb, Strings.Lbl_AcquisitionQualityReadPerformance);
        sb.AppendLine($"  {Strings.Lbl_PollSuccessRate}: {(SuccessRateDisplay is { } rate ? $"{rate:F1}%" : Strings.Lbl_N)}");
        sb.AppendLine($"  {Strings.Rtmon_P95Cycle}: {CycleP95Milliseconds} ms");
        sb.AppendLine($"  {Strings.Rtmon_P99Cycle}: {CycleP99Milliseconds} ms");

        AppendSection(sb, Strings.Lbl_HistoryWriteHealth);
        sb.AppendLine($"  {Strings.Lbl_PendingSnapshots}: {PendingHistoryCount}");
        sb.AppendLine($"  {Strings.Lbl_RecoveryFiles}: {RecoveryFileText}");
        sb.AppendLine($"  {Strings.Lbl_LastSuccessfulWrite}: {LastHistoryFlushAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? Strings.Lbl_N}");
        sb.AppendLine($"  {Strings.Lbl_WriteFailures}: {HistoryFlushFailureCount}");
        sb.AppendLine($"  {Strings.Lbl_DatabaseStorage}: {HistoryStorageText}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourcePending}: {PendingDataSourceCount}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourceRecovery}: {DataSourceRecoveryFileText}");
        sb.AppendLine($"  {Strings.Rtmon_QueuePeakOverflow}: {HistoryQueueText}");
        sb.AppendLine($"  {Strings.Rtmon_FlushLatency}: {HistoryFlushLatencyText}");
        sb.AppendLine($"  {Strings.Rtmon_DataSourceDatabase}: {DataSourceStorageText}");
        sb.AppendLine($"  {Strings.Rtmon_TotalDatabase}: {TotalStorageText}");

        AppendSection(sb, Strings.Lbl_AcquisitionProcessResourcesConfiguration);
        sb.AppendLine($"  {Strings.Lbl_CPUMemory}: {CpuMemoryText}");
        sb.AppendLine($"  {Strings.Lbl_ProgramUptime}: {ProcessUptimeText}");
        sb.AppendLine($"  {Strings.Lbl_GPUUsage}: {GpuUsageText}");
        sb.AppendLine($"  {Strings.Lbl_ConfigCheck}: {DataConsistencyText}");
        sb.AppendLine($"  {Strings.Lbl_ProcessResources}: {ProcessResourceText}");
        sb.AppendLine($"  {Strings.Lbl_MemoryDisk}: {SystemMemoryText}");
        sb.AppendLine($"  {Strings.Lbl_FreeDisk}: {DiskFreeText}");
        sb.AppendLine($"  {Strings.Rtmon_AddressConflict}: {AddressConflictCount}");
        sb.AppendLine($"  {Strings.Rtmon_InvalidAddress}: {InvalidAddressCount}");
        sb.AppendLine($"  {Strings.Lbl_StageDuration}: {StageTimingText}");
        sb.AppendLine($"  {Strings.Lbl_BatchPlan}: {BatchPlanText}");

        AppendSection(sb, Strings.Lbl_DeviceAcquisitionStatus);
        if (DeviceStatuses.Count == 0)
        {
            sb.AppendLine($"  {Strings.Lbl_NoData}");
        }
        else
        {
            // 制表符分隔：直接粘进 Excel / 工单表格能自动分列
            sb.AppendLine($"  {Strings.Lbl_Device}\t{Strings.Lbl_CurrentStatus}\t{Strings.Lbl_Round}\t{Strings.Lbl_ReadConfig}\t{Strings.Wo_OkNg}");
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

    private async Task RefreshFromLocalAsync()
    {
        LocalRefreshFrame frame;
        try
        {
            // 设备快照、库文件大小、性能计数器都在后台取，UI 线程只负责把结果写进绑定。
            frame = await Task.Run(CaptureLocalFrame);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            HandleRefreshException(ex);
            return;
        }

        if (Volatile.Read(ref _disposed) != 0) return;
        ApplyDiagnostics(frame.Diagnostics);
        ApplyConsistency(frame.Consistency);
        if (frame.ConsistencyError is not null)
            RefreshErrorMessage = frame.ConsistencyError;
    }

    private LocalRefreshFrame CaptureLocalFrame()
    {
        var acquisition = _acquisitionService.GetDiagnosticsSnapshot();
        var history = _historyService.GetDiagnosticsSnapshot();
        SystemResourceSnapshot? resources = null;
        if (_systemResourceMonitor is not null)
        {
            try
            {
                resources = _systemResourceMonitor.Sample();
            }
            catch
            {
                // 采样失败只让资源区清零，采集和历史数字仍然更新。
                resources = null;
            }
        }

        var devices = _deviceRepository.GetDevicesSnapshot();
        var diagnostics = CollectorDiagnosticsMapper.Create(
            acquisition,
            history,
            _connectionManager.IsConnected,
            _acquisitionService.IsRunning,
            _connectionManager.ConnectionStatus,
            _connectionManager.TotalDisconnectCount,
            _connectionManager.ConsecutiveFailures,
            _connectionManager.DisconnectedAt,
            devices,
            deviceId => _deviceRepository.RuntimeMap.TryGetValue(deviceId, out var runtime) ? runtime : null,
            resources);
        var (consistency, consistencyError) = ReadConsistencySafe();
        return new LocalRefreshFrame(diagnostics, consistency, consistencyError);
    }

    private sealed record LocalRefreshFrame(
        CollectorDiagnosticsDto Diagnostics,
        ConsistencyReading Consistency,
        string? ConsistencyError);

    private async Task RefreshFromRemoteAsync()
    {
        try
        {
            var diagnostics = await _remoteClient!.GetDiagnosticsAsync();
            var (consistency, consistencyError) = await Task.Run(ReadConsistencySafe);
            if (Volatile.Read(ref _disposed) != 0) return;
            ApplyDiagnostics(diagnostics);
            ApplyConsistency(consistency);
            if (consistencyError is not null)
                RefreshErrorMessage = consistencyError;
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            // 网络失败不是 PLC 采集失败：不覆盖 LastFailureMessage，断线起点也只记第一次。
            NoteCollectorUnreachable(ex.Message);
        }
    }

    private void NoteCollectorUnreachable(string message)
    {
        IsConnected = false;
        IsCollectorUnreachable = true;
        IsAcquisitionRunning = false;
        ConnectionStatus = Strings.Msg_CollectorConnected;
        _disconnectDurationSeconds = _collectorOutage.Observe(DateTime.UtcNow);
        RefreshErrorMessage = message;
        OnPropertyChanged(nameof(DisconnectDurationText));
        OnPropertyChanged(nameof(DisconnectDurationTooltip));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HasActiveFailure));
    }

    private void ApplyDiagnostics(CollectorDiagnosticsDto dto)
    {
        _collectorOutage.Clear();
        RefreshErrorMessage = null;
        IsCollectorUnreachable = false;
        IsConnected = dto.IsConnected;
        IsAcquisitionRunning = dto.IsRunning;
        ConnectionStatus = dto.ConnectionStatus;
        DisconnectedAt = dto.DisconnectedAt;
        _disconnectDurationSeconds = dto.DisconnectDurationSeconds;
        TotalDisconnectCount = dto.TotalDisconnectCount;
        ConsecutiveFailures = dto.ConsecutiveFailures;
        CompletedCycles = dto.CompletedCycles;
        FailedCycles = dto.FailedCycles;
        LastCycleSucceeded = dto.LastCycleSucceeded;
        ConsecutiveFailureCycles = dto.ConsecutiveFailureCycles;
        LastFailureAt = dto.LastFailureAt;
        LastFailureMessage = dto.LastFailureMessage;
        LastCycleMilliseconds = dto.LastCycleMilliseconds;
        AverageCycleMilliseconds = dto.AverageCycleMilliseconds;
        MaxCycleMilliseconds = dto.MaxCycleMilliseconds;
        CycleP95Milliseconds = dto.CycleP95Milliseconds;
        CycleP99Milliseconds = dto.CycleP99Milliseconds;
        LastSuccessfulDevices = dto.LastSuccessfulDevices;
        ConfiguredDevices = dto.ConfiguredDevices;
        LastSuccessfulAt = dto.LastSuccessfulAt;
        SuccessfulCycles = dto.SuccessfulCycles;
        SuccessRatePercent = dto.CompletedCycles == 0
            ? 0
            : dto.SuccessfulCycles * 100.0 / dto.CompletedCycles;
        EstimatedReadOperations = dto.EstimatedReadOperations;
        ConfiguredReadAddressCount = dto.ConfiguredReadAddressCount;
        PendingHistoryCount = dto.PendingHistoryCount;
        RecoveryFileExists = dto.RecoveryFileExists;
        RecoveryFileBytes = dto.RecoveryFileBytes;
        LastHistoryFlushAt = dto.LastHistoryFlushAt;
        HistoryFlushFailureCount = dto.HistoryFlushFailureCount;
        ProductionDatabaseBytes = dto.ProductionDatabaseBytes;
        ProductionWalBytes = dto.ProductionWalBytes;
        PendingDataSourceCount = dto.PendingDataSourceCount;
        DataSourceRecoveryFileExists = dto.DataSourceRecoveryFileExists;
        DataSourceRecoveryFileBytes = dto.DataSourceRecoveryFileBytes;
        LastDataSourceFlushAt = dto.LastDataSourceFlushAt;
        DataSourceFlushFailureCount = dto.DataSourceFlushFailureCount;
        ProductionQueuePeakCount = dto.ProductionQueuePeakCount;
        ProductionOverflowCount = dto.ProductionOverflowCount;
        DataSourceQueuePeakCount = dto.DataSourceQueuePeakCount;
        DataSourceOverflowCount = dto.DataSourceOverflowCount;
        ProductionFlushP95Milliseconds = dto.ProductionFlushP95Milliseconds;
        ProductionFlushP99Milliseconds = dto.ProductionFlushP99Milliseconds;
        DataSourceFlushP95Milliseconds = dto.DataSourceFlushP95Milliseconds;
        DataSourceFlushP99Milliseconds = dto.DataSourceFlushP99Milliseconds;
        DataSourceDatabaseBytes = dto.DataSourceDatabaseBytes;
        DataSourceWalBytes = dto.DataSourceWalBytes;
        TotalDatabaseBytes = dto.TotalDatabaseBytes;
        TotalWalBytes = dto.TotalWalBytes;
        BatchPlanRebuilds = dto.BatchPlanRebuilds;
        BatchPlanBuildMilliseconds = dto.BatchPlanBuildMilliseconds;
        DwordReadMilliseconds = dto.DWordReadMilliseconds;
        AlarmReadMilliseconds = dto.AlarmReadMilliseconds;
        DefectReadMilliseconds = dto.DefectReadMilliseconds;
        CounterAlarmReadMilliseconds = dto.CounterAlarmReadMilliseconds;
        HistoryWriteMilliseconds = dto.HistoryWriteMilliseconds;
        ApplyResources(dto);
        UpdateDeviceStatusesFromRemote(dto.DeviceStatuses);
        ApplyPollingTrend(dto.RecentCycleSamples);
        LastRefreshTime = DateTime.Now;
        NotifyDerivedMetrics();
    }

    private void NotifyDerivedMetrics()
    {
        OnPropertyChanged(nameof(DisconnectDurationText));
        OnPropertyChanged(nameof(DisconnectDurationTooltip));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HasActiveFailure));
        OnPropertyChanged(nameof(HasPollingTrendData));
        OnPropertyChanged(nameof(TotalDeviceCount));
        OnPropertyChanged(nameof(SuccessRateDisplay));
        OnPropertyChanged(nameof(SuccessRateTooltip));
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

    private void OnRefreshTimerTick()
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

    private void ApplyResources(CollectorDiagnosticsDto dto)
    {
        if (!dto.ProcessResourcesAvailable)
        {
            CpuUsagePercent = 0;
            GpuUsagePercent = 0;
            GpuAvailable = false;
            MemoryMb = 0;
            AvailableMemoryMb = 0;
            ProcessUptime = TimeSpan.Zero;
            ProcessThreadCount = 0;
            ProcessHandleCount = 0;
            FreeDiskGb = 0;
            return;
        }

        CpuUsagePercent = dto.CpuUsagePercent;
        GpuUsagePercent = dto.GpuUsagePercent;
        GpuAvailable = dto.GpuAvailable;
        MemoryMb = dto.ProcessMemoryMb;
        AvailableMemoryMb = dto.AvailableMemoryMb;
        ProcessUptime = TimeSpan.FromSeconds(dto.ProcessUptimeSeconds);
        ProcessThreadCount = dto.ProcessThreadCount;
        ProcessHandleCount = dto.ProcessHandleCount;
        FreeDiskGb = dto.FreeDiskGb;
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
                StatusText = RuntimeDeviceStatusText.Format(d.StatusWord, (int)d.OfflineCause),
                AcquisitionText = d.ConfiguredAddressCount == 0
                    ? Strings.Msg_Configured
                    : d.LastCycleSucceeded ? Strings.Msg_CycleSucceeded : Strings.Msg_CycleFailed,
                ConfiguredAddressCount = d.ConfiguredAddressCount,
                OkProduction = d.OkProduction,
                NgProduction = d.NgProduction,
            });
        }
        DeviceStatusCollectionSynchronizer.Synchronize(DeviceStatuses, desired);
    }

    private void ApplyPollingTrend(IReadOnlyList<CollectorCycleSampleDto> samples)
    {
        _pollingTrendSeries.Points.Clear();
        foreach (var sample in samples)
            _pollingTrendSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(sample.Timestamp), sample.Milliseconds));
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
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Title = Strings.Msg_Ms, TextColor = ChartPalette.MutedText, AxislineColor = OxyColors.Transparent, MajorGridlineColor = ChartPalette.Grid, MajorGridlineStyle = LineStyle.Solid });
        model.Series.Add(new LineSeries { Color = ChartPalette.Base, StrokeThickness = 2, MarkerType = MarkerType.None });
        return model;
    }

    private readonly record struct ConsistencyReading(bool Updated, int Issues, int Conflicts, int InvalidAddresses);

    /// <summary>
    /// 配置校验每 10 秒一次，并且不在 UI 线程上跑。
    /// 没有协议编解码器时直接跳过：不能悄悄用三菱规则去数别的品牌的非法地址。
    /// </summary>
    private (ConsistencyReading Reading, string? Error) ReadConsistencySafe()
    {
        try
        {
            return (ReadConsistency(), null);
        }
        catch (Exception ex)
        {
            return (default, ex.Message);
        }
    }

    private ConsistencyReading ReadConsistency()
    {
        var now = DateTime.UtcNow;
        if (now < _nextConsistencyUtc)
            return default;

        _nextConsistencyUtc = now.AddSeconds(10);
        var codec = _profileProvider?.Current.AddressCodec ?? _addressCodecResolver?.Current;
        if (codec is null)
            return default;

        var devices = _deviceRepository.GetDevicesSnapshot();
        var conflicts = DeviceConfigValidator.CollectCrossDeviceConflicts(devices, codec);
        var errors = DeviceConfigValidator.CollectValidationErrors(
            devices, codec, includeCrossDeviceConflicts: false);
        var invalid = devices.SelectMany(DeviceConfigValidator.GetDeviceAddresses).Count(address =>
            !string.IsNullOrWhiteSpace(address) && !codec.Parse(address).IsValid);
        return new ConsistencyReading(true, errors.Count + conflicts.Count, conflicts.Count, invalid);
    }

    private void ApplyConsistency(ConsistencyReading reading)
    {
        if (!reading.Updated) return;
        ConfigurationIssueCount = reading.Issues;
        AddressConflictCount = reading.Conflicts;
        InvalidAddressCount = reading.InvalidAddresses;
    }

    private static string FormatDuration(TimeSpan duration)
        => Kanban.Contracts.Formatting.DurationFormatter.FormatStandard(duration.TotalSeconds);

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1}{Strings.Msg_MB}"
        : $"{bytes / 1024d:F1}{Strings.Msg_KB}";

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        OnPageExit();
        _refreshTimer.Dispose();
        // 不释放 _systemResourceMonitor：它是 DI 单例（级联 GpuUsageMonitor），生命周期归容器。
    }
}
