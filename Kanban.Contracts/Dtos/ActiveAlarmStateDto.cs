namespace Kanban.Contracts.Dtos;

/// <summary>
/// 报警活跃状态快照（ActiveAlarmStates 表）：采集端落库的"当前触发中"权威状态。
/// 前端活跃报警墙据此直查，取代"回溯 AlarmEvents 历史事件推断活跃"的旧逻辑。
/// </summary>
public class ActiveAlarmStateDto
{
    /// <summary>设备 Id（业务关联键）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照（仅展示用）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>报警 Id：PLC 报警 = Alarm.Id；数据源报警 = src:{valueId}。</summary>
    public string AlarmId { get; set; } = string.Empty;

    /// <summary>报警名称快照（仅展示用）。</summary>
    public string AlarmName { get; set; } = string.Empty;

    public string PlcAddress { get; set; } = string.Empty;

    /// <summary>是否处于触发（活跃）状态。</summary>
    public bool IsActive { get; set; }

    /// <summary>本次连续触发的起始时刻（持续时长起点）。</summary>
    public DateTime TriggeredAt { get; set; }

    /// <summary>记录写入时所属班次快照。</summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>状态行最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; }
}