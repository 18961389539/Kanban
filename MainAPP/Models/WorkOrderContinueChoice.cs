using Kanban.Collector.Core.Entities;

namespace MainAPP.Models;

/// <summary>工单完成后的后续动作。</summary>
public enum WorkOrderContinueAction
{
    /// <summary>稍后再说：只完成当前工单，不开始下一张。</summary>
    Dismiss = 0,

    /// <summary>选择一张已有的待开始工单并开始。</summary>
    SelectExisting = 1,

    /// <summary>空白新建一张工单并开始。</summary>
    CreateNew = 2,

    /// <summary>复制刚完成的工单为新工单并开始。</summary>
    CopyCurrent = 3,
}

/// <summary>工单完成后弹窗的用户选择。</summary>
public sealed class WorkOrderContinueChoice
{
    public WorkOrderContinueAction Action { get; init; }

    /// <summary>仅 <see cref="WorkOrderContinueAction.SelectExisting"/> 时有效。</summary>
    public WorkOrder? SelectedWorkOrder { get; init; }

    public static WorkOrderContinueChoice Dismissed { get; } = new()
    {
        Action = WorkOrderContinueAction.Dismiss,
    };

    public static WorkOrderContinueChoice ForCreate { get; } = new()
    {
        Action = WorkOrderContinueAction.CreateNew,
    };

    public static WorkOrderContinueChoice ForCopy { get; } = new()
    {
        Action = WorkOrderContinueAction.CopyCurrent,
    };

    public static WorkOrderContinueChoice ForSelect(WorkOrder order) => new()
    {
        Action = WorkOrderContinueAction.SelectExisting,
        SelectedWorkOrder = order,
    };
}
