namespace Kanban.Contracts.Dtos;

/// <summary>SN 事件记录 DTO（对齐 Collector.Core 的 SnEventRecord 实体，跨进程序列化用）。</summary>
public sealed record SnEventRecordDto
{
    public int Id { get; init; }

    /// <summary>序列号（追溯查询入口）。</summary>
    public string Sn { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备名称快照（展示用）。</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>采集时刻设备 Running 工单 Id（nullable）。</summary>
    public int? WorkOrderId { get; init; }

    /// <summary>班次名称快照。</summary>
    public string ShiftName { get; init; } = string.Empty;

    /// <summary>判定结果：0=OK（良品），1=NG（不良）。</summary>
    public int Result { get; init; }

    /// <summary>事件来源（Plc=数据源触发采集；Scanner=扫码枪，预留）。</summary>
    public string Source { get; init; } = "Plc";

    /// <summary>数据源 Id（溯源）。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>批次号（预留）。</summary>
    public string? BatchNo { get; init; }

    /// <summary>采样时刻（本地时间）。</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// SN 追溯查询请求（服务端 Count + Skip/Take 分页，与历史查询同构）。
/// 过滤条件按优先级：Sn（精确）→ WorkOrderId（工单明细）→ DeviceId+时间范围。
/// </summary>
public sealed record SnEventQueryRequest
{
    /// <summary>序列号精确查询（优先级最高；命中时忽略其余过滤）。</summary>
    public string? Sn { get; init; }

    /// <summary>工单明细查询。</summary>
    public int? WorkOrderId { get; init; }

    /// <summary>设备 Id 过滤（null = 全部设备）。</summary>
    public string? DeviceId { get; init; }

    /// <summary>开始时间（含）。</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时间（含）。</summary>
    public DateTime? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;
}

/// <summary>SN 追溯查询响应（Items 按时间降序）。</summary>
public sealed record SnEventQueryResponse
{
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public IReadOnlyList<SnEventRecordDto> Items { get; init; } = [];
}
