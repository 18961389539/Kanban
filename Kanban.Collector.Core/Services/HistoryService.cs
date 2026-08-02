using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kanban.Core.Services;

public sealed record HistoryDiagnosticsSnapshot
{
    public int PendingProductionCount { get; init; }
    public bool RecoveryFileExists { get; init; }
    public long RecoveryFileBytes { get; init; }
    public DateTime? LastFlushAt { get; init; }
    public int FlushFailureCount { get; init; }
    public int TotalFlushedCount { get; init; }
    public long ProductionDatabaseBytes { get; init; }
    public long ProductionWalBytes { get; init; }
}

/// <summary>历史兼容门面；领域存储和生产写入管线由独立服务实现。</summary>
public sealed class HistoryService : IHistoryService, IHistoryQueryExecutor, IWorkOrderProductionBatchQuery, IDisposable, IAsyncDisposable
{
    private readonly ProductionHistoryStore _productionStore;
    private readonly ProductionHistoryWriter _productionWriter;
    private readonly AlarmHistoryStore _alarmStore;
    private readonly StatusTransitionHistoryStore _statusStore;
    private readonly HistoryStorageDiagnostics _storageDiagnostics;
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
        HistoryStorageDiagnostics? storageDiagnostics = null)
    {
        _db = db;
        _logger = logger;
        _productionStore = productionStore ?? new ProductionHistoryStore(db, NullLogger<ProductionHistoryStore>.Instance);
        _productionWriter = productionWriter ?? new ProductionHistoryWriter(db, settings, NullLogger<ProductionHistoryWriter>.Instance);
        _alarmStore = alarmStore ?? new AlarmHistoryStore(db, NullLogger<AlarmHistoryStore>.Instance);
        _statusStore = statusStore ?? new StatusTransitionHistoryStore(db, NullLogger<StatusTransitionHistoryStore>.Instance);
        _storageDiagnostics = storageDiagnostics ?? new HistoryStorageDiagnostics(settings);
        _ownsWriter = productionWriter is null;
        _ownsStorageDiagnostics = storageDiagnostics is null;
        _walCheckpointTimer = new Timer(_ => CheckpointWal(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public HistoryService(DatabaseProvider db, ILogger<HistoryService> logger)
        : this(db, db.AppSettings, logger) { }

    public HistoryDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var writer = _productionWriter.GetDiagnosticsSnapshot();
        var storage = _storageDiagnostics.GetSnapshot();
        return new HistoryDiagnosticsSnapshot
        {
            PendingProductionCount = writer.PendingCount,
            RecoveryFileExists = writer.RecoveryFileExists,
            RecoveryFileBytes = writer.RecoveryFileBytes,
            LastFlushAt = writer.LastFlushAt,
            FlushFailureCount = writer.FlushFailureCount,
            TotalFlushedCount = writer.TotalFlushedCount,
            ProductionDatabaseBytes = storage.ProductionDatabaseBytes,
            ProductionWalBytes = storage.ProductionWalBytes,
        };
    }

    public void LogProduction(ProductionLog log) => _productionWriter.LogProduction(log);
    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _productionStore.QueryProductionLogs(from, to, deviceId, shiftName);
    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
        => _productionStore.QueryProductionLogsByWorkOrder(workOrderId);
    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> ids)
        => _productionStore.QueryProductionLogsByWorkOrderBatch(ids);
    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
        => _productionStore.GetLatestProductionBefore(deviceId, before, shiftName);
    public List<ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => _productionStore.QueryProductionLogsStrict(from, to, deviceId, shiftName);
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
    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> ids)
        => _alarmStore.QueryAlarmEventsBatch(from, to, ids);
    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId) => _alarmStore.GetLatestAlarmEvent(alarmId);
    public int CleanupOldAlarmEvents(int retentionDays = 365) => _alarmStore.CleanupOldAlarmEvents(retentionDays);

    public bool LogStatusTransition(string deviceId, string deviceName, int previousState, int currentState,
        DateTime eventTime, string? shiftName = null)
        => _statusStore.LogStatusTransition(deviceId, deviceName, previousState, currentState, eventTime, shiftName);
    public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => _statusStore.QueryStatusTransitions(deviceId, from, to, shiftName);
    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => _statusStore.QueryStatusTransitionsStrict(deviceId, from, to, shiftName);
    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> ids)
        => _statusStore.QueryStatusTransitionsBatch(from, to, ids);
    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
        => _statusStore.GetLatestStatusBefore(deviceId, before, shiftName);
    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
        => _statusStore.GetLatestStatusBeforeStrict(deviceId, before, shiftName);
    public int CleanupOldStatusTransitions(int retentionDays = 365) => _statusStore.CleanupOldStatusTransitions(retentionDays);

    private void CheckpointWal()
    {
        try { _db.CheckpointAll(); }
        catch (Exception ex) { _logger.LogWarning(ex, "WAL checkpoint 失败"); }
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
