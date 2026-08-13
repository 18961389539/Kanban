using System.Collections.ObjectModel;
using MainAPP.Resources;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Client;
using Kanban.Core.Data;
using Kanban.Core.Models;
using MainAPP.Models;
using Kanban.Core.Services;
using MainAPP.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace MainAPP.ViewModels;

internal static class RuntimeHealthText
{
    public static string Format(bool isConnected, bool isRunning, bool lastCycleSucceeded, int consecutiveFailures)
    {
        if (!isConnected) return Strings.M013;
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
    [ObservableProperty] private int _batchPlanRebuilds;
    [ObservableProperty] private long _batchPlanBuildMilliseconds;
    [ObservableProperty] private long _dwordReadMilliseconds;
    [ObservableProperty] private long _alarmReadMilliseconds;
    [ObservableProperty] private long _defectReadMilliseconds;
    [ObservableProperty] private long _countAlarmReadMilliseconds;
    [ObservableProperty] private long _historyWriteMilliseconds;
    [ObservableProperty] private double _availableMemoryMb;
    [ObservableProperty] private int _processThreadCount;
    [ObservableProperty] private long _processHandleCount;
    [ObservableProperty] private double _freeDiskGb;
    [ObservableProperty] private PlotModel _pollingTrend = CreatePollingTrendModel();
    private LineSeries _pollingTrendSeries = null!;

    public ObservableCollection<DeviceAcquisitionStatusItem> DeviceStatuses { get; } = new();

    public string PlcEndpoint => $"{_appSettings.PlcConfig.IpAddress}:{_appSettings.PlcConfig.Port}";
    public string DisconnectDurationText => _connectionManager.DisconnectedAt is { } disconnectedAt
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
    public string StageTimingText => string.Format(Strings.F002, DwordReadMilliseconds, AlarmReadMilliseconds, DefectReadMilliseconds, CountAlarmReadMilliseconds, HistoryWriteMilliseconds);
    public string BatchPlanText => string.Format(Strings.F236, BatchPlanRebuilds, BatchPlanBuildMilliseconds);
    public string ProcessResourceText => string.Format(Strings.F186, ProcessThreadCount, ProcessHandleCount);
    public string SystemMemoryText => string.Format(Strings.F221, MemoryMb, AvailableMemoryMb);
    public string DiskFreeText => string.Format(Strings.F074, FreeDiskGb);
    public int PollingIntervalMs => _appSettings.PollingIntervalMs;
    public int HistoryWriteIntervalScans => _appSettings.HistoryWriteIntervalScans;
    public int TotalDeviceCount => _deviceRepository.GetDevicesSnapshot().Count;
    public string HealthText => RuntimeHealthText.Format(IsConnected, IsAcquisitionRunning, LastCycleSucceeded, ConsecutiveFailureCycles);
    public bool HasActiveFailure => !LastCycleSucceeded && !string.IsNullOrWhiteSpace(LastFailureMessage);

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
            RefreshFromRemote();
            return;
        }

        RefreshFromLocal();
    }

    /// <summary>本地采集模式：直接读本进程采集/历史诊断（原行为不变）。</summary>
    private void RefreshFromLocal()
    {
        var snapshot = _acquisitionService.GetDiagnosticsSnapshot();
        IsConnected = _connectionManager.IsConnected;
        IsAcquisitionRunning = _acquisitionService.IsRunning;
        ConnectionStatus = _connectionManager.ConnectionStatus;
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
        BatchPlanRebuilds = snapshot.BatchPlanRebuilds;
        BatchPlanBuildMilliseconds = snapshot.BatchPlanBuildMilliseconds;
        DwordReadMilliseconds = snapshot.DWordReadMilliseconds;
        AlarmReadMilliseconds = snapshot.AlarmReadMilliseconds;
        DefectReadMilliseconds = snapshot.DefectReadMilliseconds;
        CountAlarmReadMilliseconds = snapshot.CountAlarmReadMilliseconds;
        HistoryWriteMilliseconds = snapshot.HistoryWriteMilliseconds;

        UpdateResourceMetrics();
        MaybeUpdateConsistencyMetrics();
        UpdateDeviceStatuses(snapshot.LastSuccessfulDeviceIds);
        UpdatePollingTrend();
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
        OnPropertyChanged(nameof(StageTimingText));
        OnPropertyChanged(nameof(BatchPlanText));
        OnPropertyChanged(nameof(ProcessResourceText));
        OnPropertyChanged(nameof(SystemMemoryText));
        OnPropertyChanged(nameof(DiskFreeText));
    }

    /// <summary>Remote 模式：从 Collector 拉取诊断快照（异步，避免阻塞 UI 刷新）。</summary>
    /// <remarks>防重入：1s 定时器触发，若上一次拉取未完成（网络慢），版本守卫丢弃过期响应，
    /// 避免旧数据覆盖新数据导致数值回跳（对齐 OverviewViewModel 的 _refreshVersion 模式）。</remarks>
    private async void RefreshFromRemote()
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        try
        {
            var d = await _remoteClient!.GetDiagnosticsAsync();
            if (refreshVersion != Volatile.Read(ref _refreshVersion)) return; // 已有更新的刷新，丢弃过期响应
            IsConnected = d.IsConnected;
            // 采集状态用真值（CollectorDiagnosticsDto.IsRunning）：连接正常 ≠ 采集运行中，
            // 原先用 IsConnected 会在"连接正常但采集停止"时误报"运行中"。
            IsAcquisitionRunning = d.IsRunning;
            ConnectionStatus = d.ConnectionStatus;
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
            LastSuccessfulDevices = d.LastSuccessfulDevices;
            ConfiguredDevices = d.ConfiguredDevices;
            LastSuccessfulAt = d.LastSuccessfulAt;
            SuccessfulCycles = d.SuccessfulCycles;
            SuccessRatePercent = d.CompletedCycles == 0
                ? 0
                : d.SuccessfulCycles * 100.0 / d.CompletedCycles;
            EstimatedReadOperations = d.EstimatedReadOperations;
            PendingHistoryCount = d.PendingHistoryCount;
            RecoveryFileExists = d.RecoveryFileExists;
            RecoveryFileBytes = d.RecoveryFileBytes;
            LastHistoryFlushAt = d.LastHistoryFlushAt;
            HistoryFlushFailureCount = d.HistoryFlushFailureCount;
            ProductionDatabaseBytes = d.ProductionDatabaseBytes;
            ProductionWalBytes = d.ProductionWalBytes;
            BatchPlanRebuilds = d.BatchPlanRebuilds;
            BatchPlanBuildMilliseconds = d.BatchPlanBuildMilliseconds;
            DwordReadMilliseconds = d.DWordReadMilliseconds;
            AlarmReadMilliseconds = d.AlarmReadMilliseconds;
            DefectReadMilliseconds = d.DefectReadMilliseconds;
            CountAlarmReadMilliseconds = d.CountAlarmReadMilliseconds;
            HistoryWriteMilliseconds = d.HistoryWriteMilliseconds;

            UpdateResourceMetrics();
            MaybeUpdateConsistencyMetrics();
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
            IsAcquisitionRunning = false;
            ConnectionStatus = Strings.M050;
            OnPropertyChanged(nameof(HealthText));
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
        PollingTrend.InvalidatePlot(true);
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
        _systemResourceMonitor.Dispose();
        _pollingTrendPoints.Clear();
    }

    private readonly PollingTrendBuffer _pollingTrendPoints = new();
}
