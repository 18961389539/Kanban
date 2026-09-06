using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// Collector 采集进程诊断快照（运行监控页 Remote 模式数据源）。
/// 字段对齐 MainAPP 侧 AcquisitionDiagnosticsSnapshot + HistoryDiagnosticsSnapshot 的展示子集。
/// </summary>
public sealed record CollectorDiagnosticsDto
{
    // ── 采集循环 ──
    public int CompletedCycles { get; init; }
    public int FailedCycles { get; init; }
    public bool LastCycleSucceeded { get; init; }
    public int ConsecutiveFailureCycles { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public string? LastFailureMessage { get; init; }
    public long LastCycleMilliseconds { get; init; }
    public double AverageCycleMilliseconds { get; init; }
    public long MaxCycleMilliseconds { get; init; }
    public long CycleP95Milliseconds { get; init; }
    public long CycleP99Milliseconds { get; init; }
    public int LastSuccessfulDevices { get; init; }
    public int ConfiguredDevices { get; init; }
    public DateTime? LastSuccessfulAt { get; init; }
    public int SuccessfulCycles { get; init; }
    public int EstimatedReadOperations { get; init; }
    public int BatchPlanRebuilds { get; init; }
    public long BatchPlanBuildMilliseconds { get; init; }
    public IReadOnlyList<CollectorReaderDiagnosticsDto> DataSourceReaderDiagnostics { get; init; } = [];
    public long DWordReadMilliseconds { get; init; }
    public long AlarmReadMilliseconds { get; init; }
    public long DefectReadMilliseconds { get; init; }
    public long CounterAlarmReadMilliseconds { get; init; }
    public long HistoryWriteMilliseconds { get; init; }

    // ── 历史写入 ──
    public int PendingHistoryCount { get; init; }
    public bool RecoveryFileExists { get; init; }
    public long RecoveryFileBytes { get; init; }
    public DateTime? LastHistoryFlushAt { get; init; }
    public int HistoryFlushFailureCount { get; init; }
    public long ProductionQueuePeakCount { get; init; }
    public long ProductionOverflowCount { get; init; }
    public long ProductionFlushP95Milliseconds { get; init; }
    public long ProductionFlushP99Milliseconds { get; init; }
    public long ProductionDatabaseBytes { get; init; }
    public long ProductionWalBytes { get; init; }
    public int PendingDataSourceCount { get; init; }
    public long DataSourceQueuePeakCount { get; init; }
    public long DataSourceOverflowCount { get; init; }
    public bool DataSourceRecoveryFileExists { get; init; }
    public long DataSourceRecoveryFileBytes { get; init; }
    public long DataSourceRecoveryFileLines { get; init; }
    public DateTime? LastDataSourceFlushAt { get; init; }
    public int DataSourceFlushFailureCount { get; init; }
    public int DataSourceTotalFlushedCount { get; init; }
    public long DataSourceFlushP95Milliseconds { get; init; }
    public long DataSourceFlushP99Milliseconds { get; init; }
    public long DataSourceDatabaseBytes { get; init; }
    public long DataSourceWalBytes { get; init; }
    public long TotalDatabaseBytes { get; init; }
    public long TotalWalBytes { get; init; }

    // ── 连接状态 ──
    public bool IsConnected { get; init; }
    /// <summary>采集是否在运行（区分"连接正常但采集停止"与"采集运行中"——连接 ≠ 采集）。</summary>
    public bool IsRunning { get; init; }
    public string ConnectionStatus { get; init; } = string.Empty;
    public int TotalDisconnectCount { get; init; }
    public int ConsecutiveFailures { get; init; }
    /// <summary>最近一次断开时刻（用于显示断开持续时长）。</summary>
    public DateTime? DisconnectedAt { get; init; }
    /// <summary>Collector 侧配置的可读地址数（Remote 模式不再读本地配置）。</summary>
    public int ConfiguredReadAddressCount { get; init; }
    /// <summary>设备级采集状态明细。</summary>
    public IReadOnlyList<CollectorDeviceStatusDto> DeviceStatuses { get; init; } = Array.Empty<CollectorDeviceStatusDto>();
    /// <summary>按连接档案划分的运行状态明细（Remote 监控与 /metrics 共用同一事实源）。</summary>
    public IReadOnlyList<CollectorProfileDiagnosticsDto> Profiles { get; init; } = Array.Empty<CollectorProfileDiagnosticsDto>();
}

/// <summary>Remote 模式下单台设备的采集状态明细。</summary>
public sealed record CollectorDeviceStatusDto
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public int StatusWord { get; init; }
    public OfflineCause OfflineCause { get; init; }
    public int OkProduction { get; init; }
    public int NgProduction { get; init; }
    public int ConfiguredAddressCount { get; init; }
    public bool LastCycleSucceeded { get; init; }
}

/// <summary>单个 PLC 连接档案的运行诊断，不包含密码等敏感配置。</summary>
public sealed record CollectorProfileDiagnosticsDto
{
    public string ProfileId { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public string ProtocolKey { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public bool IsConnected { get; init; }
    public string ConnectionStatus { get; init; } = string.Empty;
    public int ConsecutiveConnectionFailures { get; init; }
    public int TotalDisconnectCount { get; init; }
    public DateTime? DisconnectedAt { get; init; }
    public TimeSpan? LastDisconnectDuration { get; init; }
    public DateTime? LastSuccessfulAcquisitionAt { get; init; }
    public DateTime? LastAcquisitionFailureAt { get; init; }
    public int ConsecutiveAcquisitionFailures { get; init; }
    public int AcquisitionFailureCount { get; init; }
    public string? LastAcquisitionFailureMessage { get; init; }
}

/// <summary>单个数据源协议 reader 的运行诊断。</summary>
public sealed record CollectorReaderDiagnosticsDto
{
    public string ProtocolKey { get; init; } = string.Empty;
    public string ReaderType { get; init; } = string.Empty;
    public int Priority { get; init; }
    public long ResolveCount { get; init; }
    public long ValidationCount { get; init; }
    public long ValidationSuccessCount { get; init; }
    public long ValidationFailureCount { get; init; }
    public long ReadCount { get; init; }
    public long ReadSuccessCount { get; init; }
    public long ReadFailureCount { get; init; }
    public long ReadP95Milliseconds { get; init; }
    public long ReadP99Milliseconds { get; init; }
    public long AcknowledgementCount { get; init; }
    public long AcknowledgementSuccessCount { get; init; }
    public long AcknowledgementFailureCount { get; init; }
    public long AcknowledgementP95Milliseconds { get; init; }
    public long AcknowledgementP99Milliseconds { get; init; }
}
