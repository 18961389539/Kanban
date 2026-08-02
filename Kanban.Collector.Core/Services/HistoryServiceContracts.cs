using Kanban.Core.Entities;

namespace Kanban.Core.Services;

/// <summary>
/// 生产快照查询能力。查询调用方只依赖此接口，不需要知道写入管线。
/// </summary>
public interface IProductionHistoryReader
{
    List<ProductionLog> QueryProductionLogs(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null);
    List<ProductionLog> QueryProductionLogsByWorkOrder(int workOrderId);
    ProductionLog? GetLatestProductionBefore(string deviceId, DateTime before, string shiftName);
    Dictionary<string, List<ProductionLog>> QueryProductionLogsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds);
}

/// <summary>生产快照兼容门面，同时提供采集线程使用的写入能力。</summary>
public interface IProductionHistoryWriter
{
    void LogProduction(ProductionLog log);
}

public interface IProductionHistoryService : IProductionHistoryReader, IProductionHistoryWriter
{
}

/// <summary>
/// 报警事件历史能力。调用方只需要报警数据时依赖此接口。
/// </summary>
public interface IAlarmHistoryService
{
    List<AlarmEventRecord> QueryAlarmEvents(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null);
    Dictionary<string, List<AlarmEventRecord>> QueryAlarmEventsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds);
    AlarmEventRecord? GetLatestAlarmEvent(string alarmId);
    bool LogAlarmEvent(string deviceId, string deviceName, string alarmId,
        string alarmName, string plcAddress, AlarmEventType eventType, DateTime eventTime,
        string? shiftName = null);
}

/// <summary>
/// 状态转换历史能力。调用方只需要状态数据时依赖此接口。
/// </summary>
public interface IStatusTransitionHistoryService
{
    List<StatusTransitionRecord> QueryStatusTransitions(string deviceId, DateTime from, DateTime to, string? shiftName = null);
    StatusTransitionRecord? GetLatestStatusBefore(string deviceId, DateTime before, string? shiftName = null);
    Dictionary<string, List<StatusTransitionRecord>> QueryStatusTransitionsBatch(DateTime from, DateTime to, IReadOnlyList<string> deviceIds);
    bool LogStatusTransition(string deviceId, string deviceName,
        int previousState, int currentState, DateTime eventTime,
        string? shiftName = null);
}
