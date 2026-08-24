namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 数据源采集快照（单表 + device_id 字段，查询按设备聚合，不做物理分表）。
/// 参照 ProductionLog 的时点快照语义：按现有历史落盘节拍定时记录各源的当前值。
/// </summary>
public class DataSourceSnapshotRecord
{
    public int Id { get; set; }

    /// <summary>设备 Id（业务关联键，快照按设备聚合查询）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照（展示用，设备改名后历史仍以 DeviceId 关联）</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>数据源 Id（业务关联键）</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>值项 Id（仅在数据源内唯一，需与 SourceId 一起定位值）。</summary>
    public string ValueId { get; set; } = string.Empty;

    /// <summary>数据源名称快照（展示用）</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>数据源类型快照（温湿度 / 电表 等，展示用）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>单位快照（如 ℃、kWh）</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>兼容旧版 Int32 采集值。</summary>
    public int Value { get; set; }

    /// <summary>值项数据类型（0=Int32,1=Float32,2=Bool,3=String）。</summary>
    public int DataType { get; set; }

    public float? FloatValue { get; set; }
    public bool? BoolValue { get; set; }
    public string? StringValue { get; set; }

    /// <summary>采样有效性（读取成功为 true）</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>班次名称快照</summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>采样时刻（本地时间，UTC 语义沿用现有历史库口径）</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>写入快照数据库的时刻，与采样时刻分离。</summary>
    public DateTime PersistedAt { get; set; } = DateTime.Now;

    /// <summary>语义化的采样时刻别名；数据库仍沿用 Timestamp 列以兼容既有历史。</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public DateTime SampledAt
    {
        get => Timestamp;
        set => Timestamp = value;
    }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public double? NumericValue => DataType switch
    {
        1 => FloatValue,
        2 => BoolValue.HasValue ? (BoolValue.Value ? 1d : 0d) : null,
        3 => null,
        _ => Value,
    };
}