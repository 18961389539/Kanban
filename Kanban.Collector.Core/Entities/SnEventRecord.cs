namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 序列号事件记录（逐件追溯的事件流）。
///
/// 与 ProductionLog / DefectSnapshotRecord 的"聚合快照"语义不同，本表是**逐件事件**：
/// 每件产品对应一行（SN 从 PLC 缓冲区经 DataSource 触发采集读出）。
/// 追溯查询按 SN / 工单 / 设备+时间范围反查，不与聚合快照混存。
/// </summary>
public class SnEventRecord
{
    public int Id { get; set; }

    /// <summary>序列号（业务关联键，追溯查询入口）。</summary>
    public string Sn { get; set; } = string.Empty;

    /// <summary>设备 Id（业务关联键，设备改名后历史仍以 DeviceId 关联）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>设备名称快照（展示用，不作为关联键）。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>采集时刻设备 Running 工单 Id（nullable：未启动工单或老数据无关联）。</summary>
    public int? WorkOrderId { get; set; }

    /// <summary>班次名称快照（写入时所属班次）。</summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>判定结果：0=OK（良品），1=NG（不良）。默认 0；由结果值项（可选）覆盖。</summary>
    public int Result { get; set; }

    /// <summary>事件来源（Plc=数据源触发采集；Scanner=扫码枪，预留）。</summary>
    public string Source { get; set; } = "Plc";

    /// <summary>数据源 Id（溯源到具体 SN 数据源配置，便于排障）。</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>批次号（预留：批次级追溯的关联键，当前版本不写入）。</summary>
    public string? BatchNo { get; set; }

    /// <summary>采样时刻（本地时间，UTC 语义沿用现有历史库口径）。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
