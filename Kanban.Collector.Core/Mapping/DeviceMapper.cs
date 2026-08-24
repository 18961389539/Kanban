using Kanban.Contracts.Dtos;
using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Models;
using ContractDataSourceValueType = Kanban.Contracts.Enums.DataSourceValueType;

namespace Kanban.Collector.Core.Mapping;

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
        ConnectionProfileId = d.ConnectionProfileId,
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
        CounterAlarms = d.CounterAlarms.Select(c => new CounterAlarmConfigDto
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
        Sources = d.Sources.Select(s => new DataSourceConfigDto
        {
            Id = s.Id,
            DeviceId = s.DeviceId,
            Name = s.Name,
            Type = s.Type,
            Enabled = s.Enabled,
            Description = s.Description,
            TriggerAddress = s.TriggerAddress,
            TriggerValue = s.TriggerValue,
            AckValue = s.AckValue,
            Values = s.Values.Select(v => new DataSourceValueConfigDto
            {
                Id = v.Id,
                Name = v.Name,
                DataType = (ContractDataSourceValueType)v.DataType,
                StringLength = v.StringLength,
                FloatLimitMin = v.FloatLimitMin,
                FloatLimitMax = v.FloatLimitMax,
                PlcAddress = v.PlcAddress,
                Unit = v.Unit,
                Enabled = v.Enabled,
                LimitMin = v.LimitMin,
                LimitMax = v.LimitMax,
                Hysteresis = v.Hysteresis,
                ConfirmSeconds = v.ConfirmSeconds,
                ExpectedValue = v.ExpectedValue,
                FloatExpectedValue = v.FloatExpectedValue,
                BoolExpectedValue = v.BoolExpectedValue,
                StringExpectedValue = v.StringExpectedValue,
                EnumValues = v.EnumValues.Select(e => new DataSourceEnumValueConfigDto
                {
                    Value = e.Value,
                    DisplayName = e.DisplayName,
                }).ToList(),
            }).ToList(),
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
            ConnectionProfileId = dto.ConnectionProfileId,
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
                DeviceId = a.DeviceId,
                Name = a.Name,
                // 先赋 PlcAddress（会触发 OnPlcAddressChanged 生成确定性 Id），
                // 最后显式赋 DTO 的 Id 覆盖之——Remote 同步必须尊重配置中的原 Id，
                // 否则自定义 GUID 的报警经一次同步后 Id 被改写，历史事件关联断裂（审查修复 2026-08-15）。
                PlcAddress = a.PlcAddress,
                Description = a.Description,
                Level = (Kanban.Collector.Core.Models.AlarmLevel)a.Level,
                Id = a.Id,
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
                Severity = (Kanban.Collector.Core.Models.DefectSeverity)x.Severity,
                Category = (Kanban.Collector.Core.Models.DefectCategory)x.Category,
            });
        }
        foreach (var c in dto.CounterAlarms ?? [])
        {
            device.CounterAlarms.Add(new CounterAlarm
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
        foreach (var s in dto.Sources ?? [])
        {
            var source = new DataSource
            {
                Id = s.Id,
                DeviceId = s.DeviceId,
                Name = s.Name,
                Type = s.Type,
                Enabled = s.Enabled,
                Description = s.Description,
                TriggerAddress = s.TriggerAddress,
                TriggerValue = s.TriggerValue,
                AckValue = s.AckValue,
            };
            foreach (var v in s.Values ?? [])
            {
                var value = new DataSourceValue
                {
                    Id = v.Id,
                    Name = v.Name,
                    DataType = (DataSourceValueType)v.DataType,
                    StringLength = v.StringLength,
                    FloatLimitMin = v.FloatLimitMin,
                    FloatLimitMax = v.FloatLimitMax,
                    PlcAddress = v.PlcAddress,
                    Unit = v.Unit,
                    Enabled = v.Enabled,
                    LimitMin = v.LimitMin,
                    LimitMax = v.LimitMax,
                    Hysteresis = v.Hysteresis,
                    ConfirmSeconds = v.ConfirmSeconds,
                    ExpectedValue = v.ExpectedValue,
                    FloatExpectedValue = v.FloatExpectedValue,
                    BoolExpectedValue = v.BoolExpectedValue,
                    StringExpectedValue = v.StringExpectedValue,
                };
                foreach (var e in v.EnumValues ?? [])
                    value.EnumValues.Add(new DataSourceEnumValue
                    {
                        Value = e.Value,
                        DisplayName = e.DisplayName,
                    });
                source.Values.Add(value);
            }
            device.Sources.Add(source);
        }
        return device;
    }
}
