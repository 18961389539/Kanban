using Kanban.Contracts.Dtos;
using Kanban.Core.Entities;

namespace Kanban.Core.Mapping;

/// <summary>
/// 工单实体 ↔ DTO 映射（**全局唯一实现**）。
/// MainAPP（Remote 工单管理/RemoteRuntimeSink 增量同步）与 Collector（ConfigSyncHandler 落库）
/// 共用此单源，禁止在别处再手写 WorkOrder↔WorkOrderDto 映射——字段新增只需改这里。
/// </summary>
public static class WorkOrderMapper
{
    public static WorkOrderDto ToDto(WorkOrder w) => new()
    {
        Id = w.Id,
        OrderNo = w.OrderNo,
        ProductCode = w.ProductCode,
        ProductName = w.ProductName,
        DeviceId = w.DeviceId,
        DeviceName = w.DeviceName,
        TargetQuantity = w.TargetQuantity,
        PlannedStart = w.PlannedStart,
        PlannedEnd = w.PlannedEnd,
        Status = (Kanban.Contracts.Enums.WorkOrderStatus)w.Status,
        CompletedOkCount = w.CompletedOkCount,
        CompletedNgCount = w.CompletedNgCount,
        Remark = w.Remark,
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt,
    };

    public static WorkOrder ToEntity(WorkOrderDto dto) => new()
    {
        Id = dto.Id,
        OrderNo = dto.OrderNo,
        ProductCode = dto.ProductCode,
        ProductName = dto.ProductName,
        DeviceId = dto.DeviceId,
        DeviceName = dto.DeviceName,
        TargetQuantity = dto.TargetQuantity,
        PlannedStart = dto.PlannedStart,
        PlannedEnd = dto.PlannedEnd,
        Status = (Kanban.Core.Entities.WorkOrderStatus)dto.Status,
        CompletedOkCount = dto.CompletedOkCount,
        CompletedNgCount = dto.CompletedNgCount,
        Remark = dto.Remark,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt,
    };
}
