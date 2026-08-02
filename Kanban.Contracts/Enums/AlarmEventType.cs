namespace Kanban.Contracts.Enums;

/// <summary>
/// 报警事件类型（与 MainAPP.Entities.AlarmEventType 数值一致，对应数据库 AlarmEvents.EventType INTEGER 列）。
/// 数值已持久化到生产数据库，禁止调整数值或插入中间值。
/// </summary>
public enum AlarmEventType
{
    /// <summary>报警触发（上升沿）</summary>
    Triggered = 1,

    /// <summary>报警恢复（下降沿）</summary>
    Recovered = 2,

    /// <summary>班次切换：当前仍触发的报警在新班次重新开始计算 Duration</summary>
    ShiftChange = 3,
}
