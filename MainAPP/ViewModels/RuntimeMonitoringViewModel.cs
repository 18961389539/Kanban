using System.Collections.ObjectModel;
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
    private readonly KanbanDataClient? _remoteClient;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>远程刷新版本守卫（RefreshFromRemote 防重入：过期响应丢弃）。</summary>
    private int _refreshVersion;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isAcquisitionRunning;
    [ObservableProperty] private string _connectionStatus = Strings.Conn_Disconnected;
    [ObservableProperty] private DateTime? _disconnectedAt;
    [ObservableProperty] private bool _isCollectorUnreachable;
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
    public bool HasActiveFailure => !IsConnected || (!LastCycleSucceeded && !string.IsNullOrWhiteSpace(LastFailureMessage));
    /// <summary>是否已有轮询趋势数据（用于空状态提示）。</summary>
    public bool HasPollingTrendData => _pollingTrendPoints.Count > 0;

    public RuntimeMonitoringViewModel(
        IPlcConnectionManager connectionManager,
        IPlcDataAcquisitionService acquisitionService,
        AppSettings appSettings,
        IDeviceRepository deviceRepository,
        HistoryService historyService,
        SystemResourceMonitor systemResourceMonitor,
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
        Refresh();
        _refreshTimer.Start();
    }

    public void OnPageExit() => _refreshTimer.Stop();

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

    /// <summary>本地采集模式：直接读本进程采集/历史诊断（原行为不变）。</summary>
    private void RefreshFromLocal()
    {
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
        OnPropertyChanged(nameof(PlcEndpoint));
        OnPropertyChanged(nameof(PollingIntervalMs));
        OnPropertyChanged(nameof(HistoryWriteIntervalScans));
        OnPropertyChanged(nameof(TotalDeviceCount));
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
            OnPropertyChanged(nameof(PlcEndpoint));
            OnPropertyChanged(nameof(PollingIntervalMs));
            OnPropertyChanged(nameof(HistoryWriteIntervalScans));
            OnPropertyChanged(nameof(TotalDeviceCount));
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
            OnPropertyChanged(nameof(PlcEndpoint));
        }
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => Refresh();

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
