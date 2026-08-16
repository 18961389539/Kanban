namespace Kanban.Collector.Core.Entities;

/// <summary>
/// 操作审计记录：谁在何时对什么做了什么、结果如何。
/// 只追加写入 audit_logs.db（<see cref="Data.AuditDbContext"/>），不提供编辑/删除接口；
/// 保留期由 AuditService.CleanupOldEntries 按 30 天滚动清理。
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }

    /// <summary>操作发生时刻（本地时间，与业务库一致）。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>操作人（用户名；未登录/系统动作为空串，如 Collector Remote 写入预留 "Remote"）。</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>操作类型（如 Auth.Login / Device.Update / WorkOrder.Start / Export.Csv）。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>操作对象类型（Device / WorkOrder / User / Settings / Export 等）。</summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>操作对象标识（设备 Id / 工单号 / 用户名 / 导出文件名等）。</summary>
    public string? TargetId { get; set; }

    /// <summary>操作结果：true=成功，false=失败。</summary>
    public bool Succeeded { get; set; } = true;

    /// <summary>补充说明（失败原因、导出条数等），最长 512 字符。</summary>
    public string? Detail { get; set; }

    /// <summary>变更前值 JSON 摘要（无对比语义的操作可为 null），最长 4000 字符。</summary>
    public string? BeforeJson { get; set; }

    /// <summary>变更后值 JSON 摘要（无对比语义的操作可为 null），最长 4000 字符。</summary>
    public string? AfterJson { get; set; }
}
