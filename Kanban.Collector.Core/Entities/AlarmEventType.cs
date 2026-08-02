namespace MainAPP.Entities;

/// <summary>
/// 报警事件类型：与数据库 AlarmEvents.EventType 字段（INTEGER）一一对应。
/// 取代历史散落各处的魔法数字 1/2/3，避免调用方传错。
/// </summary>
public enum AlarmEventType
{
    /// <summary>报警触发（上升沿）</summary>
    Triggered = 1,

    /// <summary>报警恢复（下降沿）</summary>
    Recovered = 2,

    /// <summary>班次切换：当前仍触发的报警在新班次重新开始计算 Duration</summary>
    ShiftChange = 3
}
