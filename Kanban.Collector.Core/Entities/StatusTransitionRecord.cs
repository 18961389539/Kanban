namespace MainAPP.Entities;

/// <summary>
/// 设备状态转换记录，用于 OEE 历史回溯。
/// 每次设备 StatusWord 变化时记录一条，配合班次时间边界即可精确还原各状态的累计时长。
/// PreviousState=0 表示初始状态（程序启动/班次切换后首次读取）。
/// </summary>
public class StatusTransitionRecord
{
    public int Id { get; set; }

    /// <summary>设备 Id（业务关联键）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>转换前状态（参见 DeviceStatus 常量）</summary>
    public int PreviousState { get; set; }

    /// <summary>转换后状态（参见 DeviceStatus 常量）</summary>
    public int CurrentState { get; set; }

    /// <summary>状态变化时刻。默认 DateTime.Now，避免调用方忘记赋值时写入 DateTime.MinValue（0001-01-01）。</summary>
    public DateTime EventTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 班次名称快照（记录写入时所属班次，便于按班次查询历史；
    /// 班次配置修改后不影响历史记录，仅作用于后续写入）。
    /// </summary>
    public string ShiftName { get; set; } = string.Empty;
}
