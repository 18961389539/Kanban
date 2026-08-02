namespace Kanban.Core.Services;

/// <summary>
/// 历史数据服务兼容门面：聚合生产、报警和状态转换三个领域接口。
/// 新增只依赖单一历史域的调用方应优先使用对应的窄接口；需要跨域协调的旧调用方继续依赖此门面。
/// 将业务层与具体数据访问（DatabaseProvider / EF DbContext）解耦，便于单元测试替换实现。
/// <see cref="HistoryService"/> 是其基于 SQLite/EF Core 的默认实现。
/// </summary>
public interface IHistoryService :
    IProductionHistoryService,
    IAlarmHistoryService,
    IStatusTransitionHistoryService
{
}

/// <summary>
/// 历史查询页专用的严格查询能力：数据库异常向调用方抛出，
/// 由 UI 区分“确实无数据”和“查询失败”。实时页面继续使用兼容接口的空结果回退语义。
/// </summary>
public interface IHistoryQueryExecutor
{
    List<Entities.ProductionLog> QueryProductionLogsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null);
    Entities.ProductionLog? GetLatestProductionBeforeStrict(string deviceId, DateTime before, string shiftName);
    List<Entities.AlarmEventRecord> QueryAlarmEventsStrict(DateTime from, DateTime to, string? deviceId = null, string? shiftName = null);
    List<Entities.StatusTransitionRecord> QueryStatusTransitionsStrict(string deviceId, DateTime from, DateTime to, string? shiftName = null);
    Entities.StatusTransitionRecord? GetLatestStatusBeforeStrict(string deviceId, DateTime before, string? shiftName = null);
}
