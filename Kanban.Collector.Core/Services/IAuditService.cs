using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Services;

/// <summary>
/// 操作审计服务：追加写入 audit_logs.db，并提供分页查询与保留期清理。
/// 业务代码通过 <see cref="AuditLog"/> 静态门面调用（自动带操作人），避免所有调用方持有服务引用；
/// Collector 等无 UI 会话的进程可直接注入本接口并显式传 operatorName。
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// 记录一条审计事件（异步入队，不阻塞调用方）。
    /// <paramref name="operatorName"/> 为 null 时由调用方上下文补充（MainAPP 走 AuditLog 门面自动注入）。
    /// <paramref name="beforeJson"/>/<paramref name="afterJson"/> 为变更前后值 JSON 摘要（无对比语义传 null）。
    /// </summary>
    void Record(
        string action,
        string? targetType = null,
        string? targetId = null,
        bool succeeded = true,
        string? detail = null,
        string? operatorName = null,
        string? beforeJson = null,
        string? afterJson = null);

    /// <summary>
    /// 分页查询审计记录。operatorName/action/targetType 为模糊匹配（null 表示不过滤）；
    /// succeeded 为 null 表示不过滤结果。
    /// </summary>
    (List<AuditEntry> Items, int Total) QueryPaged(
        DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded,
        int page, int pageSize);

    /// <summary>
    /// 按当前过滤条件导出全部记录（归档用，不分页）。
    /// 超过 <paramref name="maxResults"/> 条时截断，Total 返回截断前总数。
    /// </summary>
    (List<AuditEntry> Items, int Total) QueryAll(
        DateTime from, DateTime to,
        string? operatorName, string? action, string? targetType, bool? succeeded,
        int maxResults = 10000);

    /// <summary>清理早于保留期的记录，返回删除条数（默认 30 天）。</summary>
    int CleanupOldEntries(int retentionDays = 30);

    /// <summary>全部未落库条数（队列满丢弃 + 最终写入失败），供 readiness/告警判断审计链完整性。</summary>
    int DroppedCount { get; }
}
