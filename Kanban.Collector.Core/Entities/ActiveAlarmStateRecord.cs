namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 报警活跃状态（当前是否触发）——显式状态快照，替代"从 AlarmEvents 事件流推断活跃报警"的设计。
/// 采集端在报警触发/恢复边沿时写盘（Upsert 幂等），进程重启/重连后据此恢复状态；
/// 前端活跃报警墙直接查询本表，无需再回溯历史事件（杜绝孤儿报警悬空）。
/// 事件流（<see cref="AlarmEventRecord"/>）是审计记录，本表是当前真相，两者各自独立写盘。
/// </summary>
public class ActiveAlarmStateRecord
{
    public int Id { get; set; }

    /// <summary>设备 Id（业务关联键，用于查询过滤）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照（仅展示用）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// 报警 Id（业务关联键）。PLC 报警 = Alarm.Id；数据源报警 = src:{valueId}。
    /// </summary>
    public string AlarmId { get; set; } = string.Empty;

    /// <summary>报警名称快照（仅展示用）。</summary>
    public string AlarmName { get; set; } = string.Empty;

    public string PlcAddress { get; set; } = string.Empty;

    /// <summary>是否处于触发（活跃）状态。</summary>
    public bool IsActive { get; set; }

    /// <summary>本次连续触发的起始时刻（持续时长起点；班次切换时重置）。</summary>
    public DateTime TriggeredAt { get; set; } = DateTime.Now;

    /// <summary>记录写入时所属班次快照。</summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>状态行最后更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}