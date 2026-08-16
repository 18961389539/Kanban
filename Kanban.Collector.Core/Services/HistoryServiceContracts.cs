using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

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
    /// <summary>分页查询生产日志（服务端 SQL 层 Skip/Take + Count；历史查询页用，避免百万级全量传输）。</summary>
    (List<ProductionLog> Items, int Total) QueryProductionLogsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize);

    /// <summary>最新一条生产日志（SQL 层 OrderByDescending().Take(1)，替代全量拉取再内存 Take）。</summary>
    ProductionLog? QueryLatestProductionLog(DateTime from, DateTime to, string? deviceId, string? shiftName);
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

    /// <summary>严格查询：数据库异常向调用方抛出（区别于容错版本的空结果回退）。</summary>
    List<AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null);

    /// <summary>分页查询报警事件（SQL 层 Count + OrderByDescending + Skip/Take；异常向调用方抛出）。</summary>
    (List<AlarmEventRecord> Items, int Total) QueryAlarmEventsPaged(
        DateTime from, DateTime to, string? deviceId, string? shiftName, int page, int pageSize);
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

    /// <summary>分页查询状态转换记录（SQL 层 Count + OrderByDescending + Skip/Take；异常向调用方抛出）。</summary>
    (List<StatusTransitionRecord> Items, int Total) QueryStatusTransitionsPaged(
        string deviceId, DateTime from, DateTime to, string? shiftName, int page, int pageSize);
}
