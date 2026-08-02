using Kanban.Core.Entities;

namespace Kanban.Core.Services;

/// <summary>
/// 历史服务的可选批量能力，旧测试桩未实现时由工单服务回退逐条查询。
/// 随采集/存储核心迁入 Kanban.Collector.Core。
/// </summary>
public interface IWorkOrderProductionBatchQuery
{
    Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> workOrderIds);
}
