using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>报警配置 DTO（对齐 MainAPP.Models.Alarm 的序列化字段）</summary>
public sealed record AlarmConfigDto
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string PlcAddress { get; init; }
    public required string Description { get; init; }
    public required AlarmLevel Level { get; init; }
}

/// <summary>缺陷配置 DTO（对齐 MainAPP.Models.Defect 的序列化字段）</summary>
public sealed record DefectConfigDto
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string PlcAddress { get; init; }
    public required DefectSeverity Severity { get; init; }
    public required DefectCategory Category { get; init; }
}

/// <summary>计数报警配置 DTO（对齐 MainAPP.Models.CounterAlarm 的序列化字段）</summary>
public sealed record CounterAlarmConfigDto
{
    public required string Id { get; init; }
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string PlcAddress { get; init; }
    public int MaxValue { get; init; }
    public bool Enabled { get; init; }
    public required string Description { get; init; }
    public required string Unit { get; init; }
}

/// <summary>Data source value-to-display-name mapping.</summary>
public sealed record DataSourceEnumValueConfigDto
{
    public int Value { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>One typed value collected by a data source.</summary>
public sealed record DataSourceValueConfigDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DataSourceValueType DataType { get; init; }
    public int StringLength { get; init; } = 32;
    public float FloatLimitMin { get; init; }
    public float FloatLimitMax { get; init; }
    public string PlcAddress { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int LimitMin { get; init; }
    public int LimitMax { get; init; }
    public int Hysteresis { get; init; }
    public int ConfirmSeconds { get; init; } = 5;
    public int? ExpectedValue { get; init; }
    public float? FloatExpectedValue { get; init; }
    public bool? BoolExpectedValue { get; init; }
    public string? StringExpectedValue { get; init; }
    public IReadOnlyList<DataSourceEnumValueConfigDto> EnumValues { get; init; } = [];
}

/// <summary>Device-owned data source configuration.</summary>
public sealed record DataSourceConfigDto
{
    public string Id { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string Description { get; init; } = string.Empty;
    public string TriggerAddress { get; init; } = string.Empty;
    public int TriggerValue { get; init; } = 1;
    public int AckValue { get; init; } = 2;
    public IReadOnlyList<DataSourceValueConfigDto> Values { get; init; } = [];
}

/// <summary>
/// 设备配置 DTO（对齐 MainAPP.Models.Device 的序列化字段，不含运行时状态）。
/// Remote 模式下 MainAPP 设备管理页经 SignalR 同步到 Collector 落盘 devices.json。
/// </summary>
public sealed record DeviceConfigDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>设备机型/类型（配方按机型归属的关联键）。空字符串 = 通用。</summary>
    public string MachineType { get; init; } = string.Empty;
    /// <summary>设备使用的连接档案 ID；旧客户端缺省为 default。</summary>
    public string ConnectionProfileId { get; init; } = "default";
    public required string OkCountAddress { get; init; }
    public required string NgCountAddress { get; init; }
    public required string StatusCountAddress { get; init; }
    public required string ProductionResetAddress { get; init; }
    public required string RecipeName { get; init; }
    public int RecipeValue { get; init; }
    public required string RecipeAddress { get; init; }
    public int TargetCycle { get; init; }
    public IReadOnlyList<AlarmConfigDto> Alarms { get; init; } = [];
    public IReadOnlyList<DefectConfigDto> Defects { get; init; } = [];
    public IReadOnlyList<CounterAlarmConfigDto> CounterAlarms { get; init; } = [];
    public IReadOnlyList<DataSourceConfigDto> Sources { get; init; } = [];
}

/// <summary>
/// 工单 DTO（对齐 MainAPP.Entities.WorkOrder 的持久化字段，不含运行时产量聚合）。
/// Remote 模式下 MainAPP 工单管理页经 SignalR 同步到 Collector 落库 work_orders.db。
/// </summary>
public sealed record WorkOrderDto
{
    public int Id { get; init; }
    public required string OrderNo { get; init; }
    public required string ProductCode { get; init; }
    public required string ProductName { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public int TargetQuantity { get; init; }
    public DateTime PlannedStart { get; init; }
    public DateTime PlannedEnd { get; init; }
    public required WorkOrderStatus Status { get; init; }
    public int? CompletedOkCount { get; init; }
    public int? CompletedNgCount { get; init; }
    public string? Remark { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
