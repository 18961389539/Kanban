using System.ComponentModel.DataAnnotations.Schema;

namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 报警事件查询结果记录
/// </summary>
public class AlarmEventRecord
{
    public int Id { get; set; }

    /// <summary>
    /// 设备 Id（业务关联键，用于查询过滤）
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 设备名称快照（仅展示用，不作为业务查询条件，设备改名后历史数据仍以 DeviceId 关联）
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// 报警 Id（业务关联键，用于查询过滤）
    /// </summary>
    public string AlarmId { get; set; } = string.Empty;

    /// <summary>
    /// 报警名称快照（仅展示用，不作为业务查询条件）
    /// </summary>
    public string AlarmName { get; set; } = string.Empty;

    public string PlcAddress { get; set; } = string.Empty;

    /// <summary>
    /// 事件类型：1=触发, 2=恢复, 3=班次切换（报警在新班次重新开始计算）。
    /// EF Core 通过 <see cref="Data.AlarmEventDbContext"/> 中的 HasConversion&lt;int&gt; 显式按 int 列存储。
    /// 注意：枚举数值已持久化到生产数据库，禁止调整数值或插入中间值（应仅在末尾追加新值）。
    /// </summary>
    public AlarmEventType EventType { get; set; }

    /// <summary>
    /// 事件发生时刻。默认 DateTime.Now，避免调用方忘记赋值时写入 DateTime.MinValue（0001-01-01）
    /// 导致按时间范围查询漏掉这些记录、EventTime 索引出现大量 0001-01-01 条目。
    /// </summary>
    public DateTime EventTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 班次名称快照（记录写入时所属班次，便于按班次查询历史；
    /// 班次配置修改后不影响历史记录，仅作用于后续写入）。
    /// </summary>
    public string ShiftName { get; set; } = string.Empty;

    /// <summary>
    /// 显示用：本次触发持续时长文本（如 "12min" / "1.5h"）。
    /// 由查询层在贪心配对后回填，[NotMapped] 不落库。
    /// Recovered 事件为 null（表格显示 "—"）；未配对 Triggered 为 null（显示"待恢复"，语义见 AlarmInsight）。
    /// </summary>
    [NotMapped]
    public string? DurationText { get; set; }
}
