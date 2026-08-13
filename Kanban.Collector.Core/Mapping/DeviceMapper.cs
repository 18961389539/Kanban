using Kanban.Contracts.Dtos;
using Kanban.Core.Entities;
using Kanban.Core.Models;

namespace Kanban.Core.Mapping;

/// <summary>
/// 设备配置实体 ↔ DTO 映射（**全局唯一实现**）。
/// MainAPP（Remote 设备管理/屏端零配置）与 Collector（ConfigSyncHandler 落盘）共用此单源，
/// 禁止在别处再手写 Device↔DeviceConfigDto 映射——字段新增只需改这里。
/// </summary>
public static class DeviceMapper
{
    public static IReadOnlyList<DeviceConfigDto> ToDtos(IEnumerable<Device> devices)
        => devices.Select(ToDto).ToList();

    public static DeviceConfigDto ToDto(Device d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        MachineType = d.MachineType,
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

    /// <summary>DTO 列表 → 设备实体列表（Remote 屏端零配置：从 Collector 拉取的设备配置灌回内存）。</summary>
    public static List<Device> ToEntities(IReadOnlyList<DeviceConfigDto> dtos)
        => dtos.Select(ToEntity).ToList();

    public static Device ToEntity(DeviceConfigDto dto)
    {
        var device = new Device
        {
            Id = dto.Id,
            Name = dto.Name,
            MachineType = dto.MachineType,
            OkCountAddress = dto.OkCountAddress,
            NgCountAddress = dto.NgCountAddress,
            StatusCountAddress = dto.StatusCountAddress,
            ProductionResetAddress = dto.ProductionResetAddress,
            RecipeName = dto.RecipeName,
            RecipeValue = dto.RecipeValue,
            RecipeAddress = dto.RecipeAddress,
            TargetCycle = dto.TargetCycle,
        };
        foreach (var a in dto.Alarms ?? [])
        {
            device.Alarms.Add(new Alarm
            {
                Id = a.Id,
                DeviceId = a.DeviceId,
                Name = a.Name,
                PlcAddress = a.PlcAddress,
                Description = a.Description,
                Level = (Kanban.Core.Models.AlarmLevel)a.Level,
            });
        }
        foreach (var x in dto.Defects ?? [])
        {
            device.Defects.Add(new Defect
            {
                Id = x.Id,
                DeviceId = x.DeviceId,
                Name = x.Name,
                PlcAddress = x.PlcAddress,
                Severity = (Kanban.Core.Models.DefectSeverity)x.Severity,
                Category = (Kanban.Core.Models.DefectCategory)x.Category,
            });
        }
        foreach (var c in dto.CountAlarms ?? [])
        {
            device.CountAlarms.Add(new CountAlarm
            {
                Id = c.Id,
                DeviceId = c.DeviceId,
                Name = c.Name,
                PlcAddress = c.PlcAddress,
                MaxValue = c.MaxValue,
                Enabled = c.Enabled,
                Description = c.Description,
                Unit = c.Unit,
            });
        }
        return device;
    }
}
