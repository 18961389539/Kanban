namespace MainAPP.Entities;

/// <summary>
/// 生产数据快照，用于历史存储
/// </summary>
public class ProductionLog
{
    public int Id { get; set; }

    /// <summary>
    /// 设备唯一标识（外键关联 Device.Id，设备改名后历史数据仍可关联）
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 设备名称快照（便于历史查询直接展示，不作为关联键）
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// 班次名称快照（记录写入时所属班次，便于按班次查询历史；
    /// 班次配置修改后不影响历史记录，仅作用于后续写入）
    /// </summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>
    /// 关联工单 Id（外键 → WorkOrder.Id）。nullable：未启动工单或老数据无关联。
    /// 写入时由 PlcDataAcquisitionService 查询设备当前 Running 工单填充，
    /// 便于按工单统计产量/合格率，打通工单与生产数据的"信息孤岛"。
    /// </summary>
    public int? WorkOrderId { get; set; }

    /// <summary>
    /// 班次内 OK 累计产量（自本班次开始的会话累计值，= PLC 当前值 - 班次起始基线）。
    /// 非 PLC 原始累计值，可直接按班次统计产量而无需相邻快照差分。
    /// </summary>
    public int OkProduction { get; set; }

    /// <summary>
    /// 班次内 NG 累计产量（自本班次开始的会话累计值，= PLC 当前值 - 班次起始基线）
    /// </summary>
    public int NgProduction { get; set; }

    public int StatusWord { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
