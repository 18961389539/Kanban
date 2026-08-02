namespace Kanban.Contracts.Enums;

/// <summary>
/// 工单状态（与 MainAPP.Entities.WorkOrderStatus 数值一致）。
/// 状态机：Pending → Running → Completed/Aborted。
/// </summary>
public enum WorkOrderStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Aborted = 3,
}
