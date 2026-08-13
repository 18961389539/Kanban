namespace Kanban.Contracts.Dtos;

/// <summary>审计日志查询请求（只读，管理页/审计页使用）。</summary>
public sealed record AuditLogQueryRequest
{
    /// <summary>开始时间（含），null = 不限。</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时间（含），null = 不限。</summary>
    public DateTime? To { get; init; }

    /// <summary>操作人过滤（如 Remote / 用户名），null = 不限。</summary>
    public string? Operator { get; init; }

    /// <summary>操作类型过滤（如 Device.Update / WorkOrder.Upsert），null = 不限。</summary>
    public string? Action { get; init; }

    /// <summary>对象类型过滤（Device / WorkOrder / User / Settings / Export），null = 不限。</summary>
    public string? TargetType { get; init; }

    /// <summary>结果过滤（成功/失败），null = 不限。</summary>
    public bool? Succeeded { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>审计日志条目（对齐 Collector AuditEntry 实体字段）。</summary>
public sealed record AuditLogEntryDto
{
    public long Id { get; init; }
    public DateTime Timestamp { get; init; }
    public string Operator { get; init; } = "";
    public string Action { get; init; } = "";
    public string TargetType { get; init; } = "";
    public string? TargetId { get; init; }
    public bool Succeeded { get; init; } = true;
    public string? Detail { get; init; }
}

/// <summary>审计日志分页响应（服务端 Count + Skip/Take，与历史查询同构）。</summary>
public sealed record AuditLogQueryResponse
{
    public IReadOnlyList<AuditLogEntryDto> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
