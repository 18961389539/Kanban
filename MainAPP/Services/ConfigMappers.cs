using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Kanban.Contracts.Dtos;
using Kanban.Core.Entities;
using Kanban.Core.Models;
using MainAPP.Models;





namespace MainAPP.Services;

/// <summary>
/// 设备配置实体 ↔ DTO 映射（Remote 模式经 SignalR 同步设备配置到 Collector 落盘）。
/// </summary>
public static class DeviceMapper
{
    public static IReadOnlyList<DeviceConfigDto> ToDtos(IEnumerable<Device> devices)
        => devices.Select(ToDto).ToList();

    public static DeviceConfigDto ToDto(Device d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        OkCountAddress = d.OkCountAddress,
        NgCountAddress = d.NgCountAddress,
        StatusCountAddress = d.StatusCountAddress,
        ProductionResetAddress = d.ProductionResetAddress,
        RecipeName = d.RecipeName,
        RecipeValue = d.RecipeValue,
        RecipeAddress = d.RecipeAddress,
        TargetCycle = d.TargetCycle,
        Alarms = d.Alarms.Select(a => new AlarmConfigDto
        {
            Id = a.Id,
            DeviceId = a.DeviceId,
            Name = a.Name,
            PlcAddress = a.PlcAddress,
            Description = a.Description,
            Level = (Kanban.Contracts.Enums.AlarmLevel)a.Level,
        }).ToList(),
        Defects = d.Defects.Select(x => new DefectConfigDto
        {
            Id = x.Id,
            DeviceId = x.DeviceId,
            Name = x.Name,
            PlcAddress = x.PlcAddress,
            Severity = (Kanban.Contracts.Enums.DefectSeverity)x.Severity,
            Category = (Kanban.Contracts.Enums.DefectCategory)x.Category,
        }).ToList(),
        CountAlarms = d.CountAlarms.Select(c => new CountAlarmConfigDto
        {
            Id = c.Id,
            DeviceId = c.DeviceId,
            Name = c.Name,
            PlcAddress = c.PlcAddress,
            MaxValue = c.MaxValue,
            Enabled = c.Enabled,
            Description = c.Description,
            Unit = c.Unit,
        }).ToList(),
    };
}

/// <summary>
/// 工单实体 ↔ DTO 映射（Remote 模式经 SignalR 同步工单到 Collector 落库）。
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
