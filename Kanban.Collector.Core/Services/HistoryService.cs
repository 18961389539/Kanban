using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Collector.Core.Services;

public sealed record HistoryDiagnosticsSnapshot
{
    public int PendingProductionCount { get; init; }
    public bool RecoveryFileExists { get; init; }
    public long RecoveryFileBytes { get; init; }
    public long RecoveryFileLines { get; init; }
    public DateTime? LastFlushAt { get; init; }
    public int FlushFailureCount { get; init; }
    public int TotalFlushedCount { get; init; }
    public long ProductionDatabaseBytes { get; init; }
    public long ProductionWalBytes { get; init; }
    public long ProductionQueuePeakCount { get; init; }
    public long ProductionOverflowCount { get; init; }
    public long ProductionFlushP95Milliseconds { get; init; }
    public long ProductionFlushP99Milliseconds { get; init; }
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
}

/// <summary>历史兼容门面；领域存储和生产写入管线由独立服务实现。</summary>
public sealed class HistoryService : IHistoryService, IHistoryQueryExecutor, IWorkOrderProductionBatchQuery, IDisposable, IAsyncDisposable
{
    private readonly ProductionHistoryStore _productionStore;
    private readonly ProductionHistoryWriter _productionWriter;
    private readonly AlarmHistoryStore _alarmStore;
    private readonly StatusTransitionHistoryStore _statusStore;
    private readonly DefectHistoryStore _defectStore;
    private readonly HistoryStorageDiagnostics _storageDiagnostics;
    private readonly DataSourceSnapshotStore? _dataSourceSnapshotStore;
    private readonly bool _ownsWriter;
    private readonly bool _ownsStorageDiagnostics;
    private readonly Timer _walCheckpointTimer;
    private readonly ILogger<HistoryService> _logger;
    private readonly DatabaseProvider _db;

    public HistoryService(DatabaseProvider db, AppSettings settings, ILogger<HistoryService> logger,
        ProductionHistoryStore? productionStore = null,
        ProductionHistoryWriter? productionWriter = null,
        AlarmHistoryStore? alarmStore = null,
        StatusTransitionHistoryStore? statusStore = null,
        DefectHistoryStore? defectStore = null,
        HistoryStorageDiagnostics? storageDiagnostics = null,
        DataSourceSnapshotStore? dataSourceSnapshotStore = null)
    {
        _db = db;
        _logger = logger;
        _productionStore = productionStore ?? new ProductionHistoryStore(db, NullLogger<ProductionHistoryStore>.Instance);
        _productionWriter = productionWriter ?? new ProductionHistoryWriter(db, settings, NullLogger<ProductionHistoryWriter>.Instance);
        _alarmStore = alarmStore ?? new AlarmHistoryStore(db, NullLogger<AlarmHistoryStore>.Instance);
        _statusStore = statusStore ?? new StatusTransitionHistoryStore(db, NullLogger<StatusTransitionHistoryStore>.Instance);
        _defectStore = defectStore ?? new DefectHistoryStore(db, NullLogger<DefectHistoryStore>.Instance);
        _storageDiagnostics = storageDiagnostics ?? new HistoryStorageDiagnostics(settings);
        _dataSourceSnapshotStore = dataSourceSnapshotStore;
        _ownsWriter = productionWriter is null;
        _ownsStorageDiagnostics = storageDiagnostics is null;
        _walCheckpointTimer = new Timer(_ => CheckpointWal(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HistoryService(DatabaseProvider db, ILogger<HistoryService> logger)
        : this(db, db.AppSettings, logger) { }

    public HistoryDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var writer = _productionWriter.GetDiagnosticsSnapshot();
        var dataSourceWriter = _dataSourceSnapshotStore?.GetDiagnosticsSnapshot()
            ?? new DataSourceSnapshotWriterDiagnosticsSnapshot();
        var storage = _storageDiagnostics.GetSnapshot();
        return new HistoryDiagnosticsSnapshot
        {
            PendingProductionCount = writer.PendingCount,
            RecoveryFileExists = writer.RecoveryFileExists,
            RecoveryFileBytes = writer.RecoveryFileBytes,
            RecoveryFileLines = writer.RecoveryFileLines,
            LastFlushAt = writer.LastFlushAt,
            FlushFailureCount = writer.FlushFailureCount,
            TotalFlushedCount = writer.TotalFlushedCount,
            ProductionQueuePeakCount = writer.QueuePeakCount,
            ProductionOverflowCount = writer.OverflowCount,
            ProductionFlushP95Milliseconds = writer.FlushP95Milliseconds,
            ProductionFlushP99Milliseconds = writer.FlushP99Milliseconds,
            ProductionDatabaseBytes = storage.ProductionDatabaseBytes,
            ProductionWalBytes = storage.ProductionWalBytes,
            PendingDataSourceCount = dataSourceWriter.PendingCount,
            DataSourceQueuePeakCount = dataSourceWriter.QueuePeakCount,
            DataSourceOverflowCount = dataSourceWriter.OverflowCount,
            DataSourceRecoveryFileExists = dataSourceWriter.RecoveryFileExists,
            DataSourceRecoveryFileBytes = dataSourceWriter.RecoveryFileBytes,
            DataSourceRecoveryFileLines = dataSourceWriter.RecoveryFileLines,
            LastDataSourceFlushAt = dataSourceWriter.LastFlushAt,
            DataSourceFlushFailureCount = dataSourceWriter.FlushFailureCount,
            DataSourceTotalFlushedCount = dataSourceWriter.TotalFlushedCount,
            DataSourceFlushP95Milliseconds = dataSourceWriter.FlushP95Milliseconds,
            DataSourceFlushP99Milliseconds = dataSourceWriter.FlushP99Milliseconds,
            DataSourceDatabaseBytes = storage.DataSourceDatabaseBytes,
            DataSourceWalBytes = storage.DataSourceWalBytes,
            TotalDatabaseBytes = storage.TotalDatabaseBytes,
            TotalWalBytes = storage.TotalWalBytes,
        };
    }

    public void LogProduction(ProductionLog log) => _productionWriter.LogProduction(log);
    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _productionStore.QueryProductionLogs(from, to, deviceId, shiftName);
    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
        => _productionStore.QueryProductionLogsByWorkOrder(workOrderId);
    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> ids)
        => _productionStore.QueryProductionLogsByWorkOrderBatch(ids);
    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByDeviceWindowsBatch(
        IReadOnlyList<(int WorkOrderId, string DeviceId, DateTime From, DateTime To)> windows)
        => _productionStore.QueryProductionLogsByDeviceWindowsBatch(windows);
    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
        => _productionStore.GetLatestProductionBefore(deviceId, before, shiftName);
    public List<ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _productionStore.QueryProductionLogsStrict(from, to, deviceId, shiftName);

    /// <summary>分页查询生产日志（服务端 SQL 层 Skip/Take + Count；历史查询页用，避免百万级全量传输）。</summary>
    public (List<ProductionLog> Items, int Total) QueryProductionLogsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
        => _productionStore.QueryProductionLogsPaged(from, to, deviceId, shiftName, page, pageSize);

    /// <summary>最新一条生产日志（SQL 层 Take(1)）。</summary>
    public ProductionLog? QueryLatestProductionLog(DateTime from, DateTime to, string? deviceId, string? shiftName)
        => _productionStore.QueryLatestProductionLog(from, to, deviceId, shiftName);
    public ProductionLog? GetLatestProductionBeforeStrict(string deviceId, DateTime before, string shiftName)
        => _productionStore.GetLatestProductionBeforeStrict(deviceId, before, shiftName);
    public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> ids)
        => _productionStore.QueryProductionLogsBatch(from, to, ids);
    public int CleanupOldProductionLogs(int retentionDays = 365)
        => _productionStore.CleanupOldProductionLogs(retentionDays);

    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId, string alarmName, string plcAddress,
        AlarmEventType eventType, DateTime eventTime, string? shiftName = null)
        => _alarmStore.LogAlarmEvent(deviceId, deviceName, alarmId, alarmName, plcAddress, eventType, eventTime, shiftName);
    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _alarmStore.QueryAlarmEvents(from, to, deviceId, shiftName);
    public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _alarmStore.QueryAlarmEventsStrict(from, to, deviceId, shiftName);
    public (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize)
        => _alarmStore.QueryAlarmEventsPaged(from, to, deviceId, shiftName, page, pageSize);
    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> ids)
        => _alarmStore.QueryAlarmEventsBatch(from, to, ids);
    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId) => _alarmStore.GetLatestAlarmEvent(alarmId);
    public AlarmEventRecord? GetLatestAlarmEventStrict(string alarmId) => _alarmStore.GetLatestAlarmEventStrict(alarmId);
    public int CleanupOldAlarmEvents(int retentionDays = 365) => _alarmStore.CleanupOldAlarmEvents(retentionDays);

    public bool LogStatusTransition(string deviceId, string deviceName, int previousState, int currentState,
        DateTime eventTime, string? shiftName = null)
        => _statusStore.LogStatusTransition(deviceId, deviceName, previousState, currentState, eventTime, shiftName);
    public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => _statusStore.QueryStatusTransitions(deviceId, from, to, shiftName);
    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => _statusStore.QueryStatusTransitionsStrict(deviceId, from, to, shiftName);
    public (List<StatusTransitionRecord> Items, int Total) QueryStatusTransitionsPaged(
        string deviceId, DateTime from, DateTime to, string? shiftName, int page, int pageSize)
        => _statusStore.QueryStatusTransitionsPaged(deviceId, from, to, shiftName, page, pageSize);
    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> ids)
        => _statusStore.QueryStatusTransitionsBatch(from, to, ids);
    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
        => _statusStore.GetLatestStatusBefore(deviceId, before, shiftName);
    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
        => _statusStore.GetLatestStatusBeforeStrict(deviceId, before, shiftName);
    public int CleanupOldStatusTransitions(int retentionDays = 365) => _statusStore.CleanupOldStatusTransitions(retentionDays);

    /// <summary>分页查询缺陷快照（SQL 层 Count + Skip/Take；异常向调用方抛出）。</summary>
    public (List<DefectSnapshotRecord> Items, int Total) QueryDefectSnapshotsPaged(
        DateTime from, DateTime to, string deviceId, int page, int pageSize)
        => _defectStore.QueryDefectSnapshotsPaged(from, to, deviceId, page, pageSize);

    private void CheckpointWal()
    {
        try { _db.CheckpointAll(); }
        catch (Exception ex) { _logger.LogWarning(ex, "WAL checkpoint 失败"); }
    }

    /// <summary>
    /// 按保留天数分批清理四类历史数据（生产/报警/状态/缺陷），返回各类删除数量合计。
    /// 供 Collector 的保留策略服务启动与定时调用；0 或负数表示禁用清理。
    /// </summary>
    public int CleanupOldHistory(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var total = 0;
        total += CleanupOldProductionLogs(retentionDays);
        total += CleanupOldAlarmEvents(retentionDays);
        total += CleanupOldStatusTransitions(retentionDays);
        total += _defectStore.CleanupOldSnapshots(retentionDays);
        CheckpointWal();
        return total;
    }

    public void Dispose()
    {
        _walCheckpointTimer.Dispose();
        if (_ownsWriter) _productionWriter.Dispose();
        if (_ownsStorageDiagnostics) _storageDiagnostics.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        _walCheckpointTimer.Dispose();
        if (_ownsWriter) await _productionWriter.DisposeAsync();
        if (_ownsStorageDiagnostics) _storageDiagnostics.Dispose();
        GC.SuppressFinalize(this);
    }
}
