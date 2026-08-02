using Kanban.Contracts.Dtos;
using Kanban.Contracts.Enums;
using MainAPP.Entities;
using MainAPP.Models;
using Microsoft.Extensions.Logging;
using DeviceStatus = Kanban.Contracts.Enums.DeviceStatus;
using DefectSeverity = Kanban.Contracts.Enums.DefectSeverity;
using DefectCategory = Kanban.Contracts.Enums.DefectCategory;

namespace MainAPP.Services;

/// <summary>
/// 历史查询路由代理（展示端瘦身核心）：实现全部历史接口，
/// Local 模式委托本地 <see cref="HistoryService"/>（SQLite，行为不变），
/// Remote 模式通过 <see cref="KanbanDataClient.QueryHistoryAsync"/> 走 SignalR 到 Collector。
/// ViewModel 只依赖接口，切换模式无需改任何调用方。
/// 写方法（Log*）在 Remote 模式下无意义（写入只在 Collector 采集管线发生），记录警告并忽略。
/// </summary>
public sealed class RemoteHistoryQueryService :
    IHistoryService,
    IHistoryQueryExecutor,
    IWorkOrderProductionBatchQuery
{
    private readonly HistoryService _local;
    private readonly KanbanDataClient _client;
    private readonly AppSettings _settings;
    private readonly ILogger<RemoteHistoryQueryService> _logger;

    public RemoteHistoryQueryService(
        HistoryService local,
        KanbanDataClient client,
        AppSettings settings,
        ILogger<RemoteHistoryQueryService> logger)
    {
        _local = local;
        _client = client;
        _settings = settings;
        _logger = logger;
    }

    private bool IsRemote => _settings.DataMode == KanbanDataMode.Remote;

    // ──────────── IProductionHistoryReader ────────────

    public List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName)
            : _local.QueryProductionLogs(from, to, deviceId, shiftName);

    public List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, DateTime.MaxValue, null, null, workOrderId: workOrderId)
            : _local.QueryProductionLogsByWorkOrder(workOrderId);

    public ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestProductionBefore(deviceId, before, shiftName);

    public Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? deviceIds.ToDictionary(id => id, id => QueryProductionLogs(from, to, id))
            : _local.QueryProductionLogsBatch(from, to, deviceIds);

    // ──────────── IAlarmHistoryService ────────────

    public List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName)
            : _local.QueryAlarmEvents(from, to, deviceId, shiftName);

    public Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? deviceIds.ToDictionary(id => id, id => QueryAlarmEvents(from, to, id))
            : _local.QueryAlarmEventsBatch(from, to, deviceIds);

    public AlarmEventRecord? GetLatestAlarmEvent(string alarmId)
        => IsRemote
            ? QueryRemoteAlarmByAlarmId(alarmId)
            : _local.GetLatestAlarmEvent(alarmId);

    // ──────────── IStatusTransitionHistoryService ────────────

    public List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName)
            : _local.QueryStatusTransitions(deviceId, from, to, shiftName);

    public Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds)
        => IsRemote
            ? deviceIds.ToDictionary(id => id, id => QueryStatusTransitions(id, from, to))
            : _local.QueryStatusTransitionsBatch(from, to, deviceIds);

    public StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestStatusBefore(deviceId, before, shiftName);

    // ──────────── IProductionHistoryWriter（Remote 模式忽略） ────────────

    public void LogProduction(ProductionLog log)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogProduction（写入仅在 Collector 采集管线）");
            return;
        }
        _local.LogProduction(log);
    }

    public bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, Entities.AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogAlarmEvent Alarm={AlarmId}", alarmId);
            return false;
        }
        return _local.LogAlarmEvent(deviceId, deviceName, alarmId, alarmName, plcAddress, eventType, eventTime, shiftName);
    }

    public bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null)
    {
        if (IsRemote)
        {
            _logger.LogWarning("Remote 模式忽略 LogStatusTransition Device={DeviceId}", deviceId);
            return false;
        }
        return _local.LogStatusTransition(deviceId, deviceName, previousState, currentState, eventTime, shiftName);
    }

    // ──────────── IHistoryQueryExecutor（Strict：Remote 网络异常直接抛出） ────────────

    public List<ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, from, to, deviceId, shiftName)
            : _local.QueryProductionLogsStrict(from, to, deviceId, shiftName);

    public ProductionLog? GetLatestProductionBeforeStrict(string deviceId, DateTime before, string shiftName)
        => IsRemote
            ? QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestProductionBeforeStrict(deviceId, before, shiftName);

    public List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<AlarmEventRecord>(HistoryQueryType.AlarmEvent, from, to, deviceId, shiftName)
            : _local.QueryAlarmEventsStrict(from, to, deviceId, shiftName);

    public List<StatusTransitionRecord> QueryStatusTransitionsStrict(string deviceId, DateTime from, DateTime to, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, from, to, deviceId, shiftName)
            : _local.QueryStatusTransitionsStrict(deviceId, from, to, shiftName);

    public StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null)
        => IsRemote
            ? QueryRemoteList<StatusTransitionRecord>(HistoryQueryType.StatusTransition, DateTime.MinValue, before, deviceId, shiftName, latestFirst: true).FirstOrDefault()
            : _local.GetLatestStatusBeforeStrict(deviceId, before, shiftName);

    // ──────────── IWorkOrderProductionBatchQuery ────────────

    public Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> workOrderIds)
    {
        if (!IsRemote)
            return _local.QueryProductionLogsByWorkOrderBatch(workOrderIds);

        var result = new Dictionary<int, List<ProductionLog>>();
        foreach (var id in workOrderIds)
        {
            var logs = QueryRemoteList<ProductionLog>(HistoryQueryType.ProductionLog, DateTime.MinValue, DateTime.MaxValue, null, null, workOrderId: id);
            result[id] = logs;
        }
        return result;
    }

    // ──────────── Remote 查询核心 ────────────

    private List<T> QueryRemoteList<T>(
        HistoryQueryType type,
        DateTime from,
        DateTime to,
        string? deviceId,
        string? shiftName,
        bool latestFirst = false,
        int? workOrderId = null)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = type,
            From = from == DateTime.MinValue ? null : from,
            To = to == DateTime.MaxValue ? null : to,
            DeviceId = deviceId,
            ShiftName = shiftName,
            LatestFirst = latestFirst,
            WorkOrderId = workOrderId,
            Page = 1,
            PageSize = int.MaxValue,
        };
        return InvokeAndMap<T>(request);
    }

    private AlarmEventRecord? QueryRemoteAlarmByAlarmId(string alarmId)
    {
        var request = new HistoryQueryRequest
        {
            QueryType = HistoryQueryType.AlarmEvent,
            AlarmId = alarmId,
            Page = 1,
            PageSize = 1,
        };
        return InvokeAndMap<AlarmEventRecord>(request).FirstOrDefault();
    }

    private List<T> InvokeAndMap<T>(HistoryQueryRequest request)
    {
        // SignalR InvokeAsync 是异步的；EF 查询在服务端同步执行，这里等待即可
        var response = _client.QueryHistoryAsync(request).GetAwaiter().GetResult();
        return MapDtos<T>(response);
    }

    private static List<T> MapDtos<T>(HistoryQueryResponse response)
    {
        if (typeof(T) == typeof(ProductionLog))
            return response.ProductionLogs
                .Select(dto => (T)(object)new ProductionLog
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    ShiftName = dto.ShiftName,
                    WorkOrderId = dto.WorkOrderId,
                    OkProduction = dto.OkProduction,
                    NgProduction = dto.NgProduction,
                    StatusWord = dto.StatusWord,
                    Timestamp = dto.Timestamp,
                }).ToList();

        if (typeof(T) == typeof(AlarmEventRecord))
            return response.AlarmEvents
                .Select(dto => (T)(object)new AlarmEventRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    AlarmId = dto.AlarmId,
                    AlarmName = dto.AlarmName,
                    PlcAddress = dto.PlcAddress,
                    EventType = (Entities.AlarmEventType)dto.EventType,
                    EventTime = dto.EventTime,
                    ShiftName = dto.ShiftName,
                }).ToList();

        if (typeof(T) == typeof(StatusTransitionRecord))
            return response.StatusTransitions
                .Select(dto => (T)(object)new StatusTransitionRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    PreviousState = (int)dto.PreviousState,
                    CurrentState = (int)dto.CurrentState,
                    EventTime = dto.EventTime,
                    ShiftName = dto.ShiftName,
                }).ToList();

        if (typeof(T) == typeof(DefectSnapshotRecord))
            return response.DefectSnapshots
                .Select(dto => (T)(object)new DefectSnapshotRecord
                {
                    Id = dto.Id,
                    DeviceId = dto.DeviceId,
                    DeviceName = dto.DeviceName,
                    DefectId = dto.DefectId,
                    DefectName = dto.DefectName,
                    Severity = (Models.DefectSeverity)(int)dto.Severity,
                    Category = (Models.DefectCategory)(int)dto.Category,
                    ShiftName = dto.ShiftName,
                    Count = dto.Count,
                    Timestamp = dto.Timestamp,
                }).ToList();

        return [];
    }
}
