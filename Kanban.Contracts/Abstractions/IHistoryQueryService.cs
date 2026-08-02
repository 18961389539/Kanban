using Kanban.Contracts.Dtos;

namespace Kanban.Contracts.Abstractions;

/// <summary>
/// 历史查询服务：SQLite 只被 Collector 持锁，展示端通过此接口查询。
/// </summary>
public interface IHistoryQueryService
{
    Task<HistoryQueryResponse> QueryAsync(HistoryQueryRequest request, CancellationToken cancellationToken = default);
}
