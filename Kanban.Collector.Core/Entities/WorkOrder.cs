namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 工单状态枚举。状态机：Pending → Running → Completed/Aborted。
/// 转换需在 Service/ViewModel 层校验，避免 UI 直接改状态。
/// </summary>
public enum WorkOrderStatus
{
    /// <summary>待开始：已创建但尚未启动。</summary>
    Pending = 0,
    /// <summary>进行中：当前正在生产。</summary>
    Running = 1,
    /// <summary>已完成：达到目标产量或人工结束。</summary>
    Completed = 2,
    /// <summary>已中止：异常终止（如换型/报废）。</summary>
    Aborted = 3,
}

/// <summary>
/// 工单实体：表示一次生产任务。
/// 持久化到独立数据库 work_orders.db（由 <see cref="Data.WorkOrderDbContext"/> 管理）。
/// 与 <see cref="Models.Device"/> 通过 DeviceId 关联（1 台设备同时只能有 1 个 Running 工单）。
/// Running 期间产量按 DeviceId + 时间区间从 ProductionLog 实时聚合；
/// Completed/Aborted 时产量快照写入 CompletedOkCount/CompletedNgCount，避免历史工单重复扫描日志。
/// </summary>
public class WorkOrder
{
    public int Id { get; set; }

    /// <summary>工单号（人工录入或扫码，业务主键）。允许重复以兼容简化工单场景。</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>产品编码（与设备配方 RecipeName 互补：配方是设备默认值，工单是本次生产的具体值）。</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>产品名称（便于展示，不作为关联键）。</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>绑定设备 Id（外键关联 Device.Id）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照（便于工单列表直接展示，不作为关联键）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>计划产量（目标件数）。</summary>
    public int TargetQuantity { get; set; }

    /// <summary>计划开始时间。</summary>
    public DateTime PlannedStart { get; set; }

    /// <summary>计划结束时间。</summary>
    public DateTime PlannedEnd { get; set; }

    /// <summary>工单状态（Pending/Running/Completed/Aborted）。</summary>
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Pending;

    /// <summary>
    /// 完成时合格产量快照（Completed/Aborted 时由 WorkOrderService 写入）。
    /// Null 表示未结束或老数据未回填，此时 GetProductionSummary 回退按日志聚合。
    /// 持久化后即使 ProductionLogs 被清理，工单历史产量仍可读。
    /// </summary>
    public int? CompletedOkCount { get; set; }

    /// <summary>完成时不良产量快照（同 CompletedOkCount）。</summary>
    public int? CompletedNgCount { get; set; }

    /// <summary>备注。</summary>
    public string? Remark { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 运行时产量聚合（非持久化，由 ViewModel 查询后回填用于 UI 绑定）。
    /// 使用 INotifyPropertyChanged 通知 UI 更新进度条/产量文本。
    /// EF Core [NotMapped] 确保不写入数据库。
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public WorkOrderRuntimeProduction? Production { get; set; }

    /// <summary>开始工单：Pending → Running。违反状态约束则抛 InvalidOperationException。</summary>
    public void Start()
    {
        if (Status != WorkOrderStatus.Pending)
            throw new InvalidOperationException($"工单 {OrderNo} 当前状态为 {Status}，无法开始（仅 Pending 可开始）");
        Status = WorkOrderStatus.Running;
        UpdatedAt = DateTime.Now;
    }

    /// <summary>完成工单：Running → Completed。违反状态约束则抛 InvalidOperationException。</summary>
    public void Complete()
    {
        if (Status != WorkOrderStatus.Running)
            throw new InvalidOperationException($"工单 {OrderNo} 当前状态为 {Status}，无法完成（仅 Running 可完成）");
        Status = WorkOrderStatus.Completed;
        UpdatedAt = DateTime.Now;
    }

    /// <summary>中止工单：Running/Pending → Aborted。违反状态约束则抛 InvalidOperationException。</summary>
    public void Abort()
    {
        if (Status != WorkOrderStatus.Running && Status != WorkOrderStatus.Pending)
            throw new InvalidOperationException($"工单 {OrderNo} 当前状态为 {Status}，无法中止（仅 Running/Pending 可中止）");
        Status = WorkOrderStatus.Aborted;
        UpdatedAt = DateTime.Now;
    }
}

/// <summary>
/// 工单运行时产量数据（非持久化）：用于列表项进度条与详情页产量展示。
/// 由 IWorkOrderService.GetProductionSummary 查询后构造，回填到 WorkOrder.Production。
/// </summary>
public sealed class WorkOrderRuntimeProduction
{
    /// <summary>合格产量。</summary>
    public int OkCount { get; init; }

    /// <summary>不良产量。</summary>
    public int NgCount { get; init; }

    /// <summary>总产量 = OkCount + NgCount。</summary>
    public int TotalCount => OkCount + NgCount;

    /// <summary>达成率（0~1）= OkCount / TargetQuantity。</summary>
    public double AchievementRate { get; init; }

    /// <summary>计划进度状态：正常、进度落后、即将超期或已超期。</summary>
    public string ScheduleStatusText { get; init; } = string.Empty;

    /// <summary>实际达成率与时间理论进度的偏差。</summary>
    public double ProgressDeviation { get; init; }
}
