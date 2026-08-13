using Kanban.Core.Entities;

namespace Kanban.Core.Services;

/// <summary>
/// 历史服务的可选批量能力，旧测试桩未实现时由工单服务回退逐条查询。
/// 随采集/存储核心迁入 Kanban.Collector.Core。
/// </summary>
public interface IWorkOrderProductionBatchQuery
{
    Dictionary<int, List<ProductionLog>> QueryProductionLogsByWorkOrderBatch(IReadOnlyList<int> workOrderIds);

    /// <summary>
    /// 按 (工单 Id, 设备 Id, 时间窗口) 批量查询生产日志（审查修复 2026-08-13）：
    /// 工单产量聚合的回退路径（老数据无 WorkOrderId 关联 / 未落库新工单）此前 Remote 模式
    /// 逐条 GetProductionSummary → N+1 次 UI 线程同步 SignalR 往返；本方法合并为批量请求。
    /// 返回按 WorkOrderId 分组的结果（无日志的工单不出现在字典中）。
    /// </summary>
    Dictionary<int, List<ProductionLog>> QueryProductionLogsByDeviceWindowsBatch(
        IReadOnlyList<(int WorkOrderId, string DeviceId, DateTime From, DateTime To)> windows);
}
