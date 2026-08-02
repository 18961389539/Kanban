using Kanban.Contracts.Enums;

namespace Kanban.Contracts.Dtos;

/// <summary>
/// 历史查询类型。
/// </summary>
public enum HistoryQueryType
{
    ProductionLog = 0,
    AlarmEvent = 1,
    StatusTransition = 2,
    DefectSnapshot = 3,
}

/// <summary>
/// 历史查询请求（Unary，客户端请求-响应）。
/// </summary>
public sealed record HistoryQueryRequest
{
    public required HistoryQueryType QueryType { get; init; }

    /// <summary>设备 Id 过滤（null = 全部设备）</summary>
    public string? DeviceId { get; init; }

    /// <summary>开始时间（含）</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时间（含）</summary>
    public DateTime? To { get; init; }

    /// <summary>班次名称过滤（null = 全部班次）</summary>
    public string? ShiftName { get; init; }

    /// <summary>工单 Id 过滤（仅生产日志有效）</summary>
    public int? WorkOrderId { get; init; }

    /// <summary>报警 Id 过滤（仅报警事件有效，GetLatestAlarmEvent 语义）</summary>
    public string? AlarmId { get; init; }

    /// <summary>是否只取最新一条（按时间倒序第一条，配合 To 实现 GetLatest* 语义）。默认 false。</summary>
    public bool LatestFirst { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// 历史查询响应。按 QueryType 填充对应强类型列表
/// （SignalR 序列化 object 装箱集合会退化为 JsonElement，故必须用强类型字段）。
/// </summary>
public sealed record HistoryQueryResponse
{
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }

    public IReadOnlyList<ProductionLogDto> ProductionLogs { get; init; } = [];
    public IReadOnlyList<AlarmEventRecordDto> AlarmEvents { get; init; } = [];
    public IReadOnlyList<StatusTransitionRecordDto> StatusTransitions { get; init; } = [];
    public IReadOnlyList<DefectSnapshotRecordDto> DefectSnapshots { get; init; } = [];
}

/// <summary>生产日志记录 DTO（对齐 ProductionLog 实体）</summary>
public sealed record ProductionLogDto
{
    public int Id { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string ShiftName { get; init; }
    public int? WorkOrderId { get; init; }
    public int OkProduction { get; init; }
    public int NgProduction { get; init; }
    public int StatusWord { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>报警历史记录 DTO（对齐 AlarmEventRecord 实体）</summary>
public sealed record AlarmEventRecordDto
{
    public int Id { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string AlarmId { get; init; }
    public required string AlarmName { get; init; }
    public required string PlcAddress { get; init; }
    public required AlarmEventType EventType { get; init; }
    public DateTime EventTime { get; init; }
    public required string ShiftName { get; init; }
}

/// <summary>状态转换记录 DTO（对齐 StatusTransitionRecord 实体）</summary>
public sealed record StatusTransitionRecordDto
{
    public int Id { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required DeviceStatus PreviousState { get; init; }
    public required DeviceStatus CurrentState { get; init; }
    public DateTime EventTime { get; init; }
    public required string ShiftName { get; init; }
}

/// <summary>缺陷快照记录 DTO（对齐 DefectSnapshotRecord 实体）</summary>
public sealed record DefectSnapshotRecordDto
{
    public int Id { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string DefectId { get; init; }
    public required string DefectName { get; init; }
    public required DefectSeverity Severity { get; init; }
    public required DefectCategory Category { get; init; }
    public required string ShiftName { get; init; }
    public int Count { get; init; }
    public DateTime Timestamp { get; init; }
}
