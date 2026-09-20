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

    /// <summary>缺陷快照按小时分组下推（每组取小时末值 + 窗口前基线），供复盘页缺陷集中度使用。</summary>
    DefectSnapshotHourly = 4,
}

/// <summary>
/// 历史查询错误码（跨进程契约）：客户端据此渲染本地化错误文案，
/// Error 字段只承载服务端调试细节（客户端不得直接展示，避免多语言界面收到中文回显）。
/// </summary>
public enum HistoryErrorCode
{
    /// <summary>无错误（正常结果或真实空数据）</summary>
    None = 0,

    /// <summary>服务端查询失败（落库/IO/参数异常，细节在 Error 字段，仅进服务端/客户端日志）</summary>
    QueryFailed = 1,
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

    /// <summary>报警名称过滤（仅报警事件有效；历史查询页下拉与 WPF 同口径）</summary>
    public string? AlarmName { get; init; }

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
    /// <summary>查询错误码（None=成功/空数据；QueryFailed=服务端失败，见 Error 细节）。客户端按码渲染本地化文案。</summary>
    public HistoryErrorCode ErrorCode { get; init; }

    /// <summary>查询错误消息（null=成功）。服务端把落库/IO 异常转为结构化错误返回，
    /// 客户端据此区分"真实空数据"与"查询失败"，避免把故障当空结果显示。
    /// 仅用于服务端/客户端日志；用户可见文案由客户端按 <see cref="ErrorCode"/> 本地化渲染。</summary>
    public string? Error { get; init; }

    /// <summary>时间窗口是否被服务端截断（请求跨度超过 <see cref="HistoryQueryLimits.MaxQueryWindowDays"/>
    /// 时静默收窄为最近窗口）。客户端可据此提示"结果已按最近 N 天截断"，避免对账误判。</summary>
    public bool IsWindowTruncated { get; init; }

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

/// <summary>
/// 批量历史查询请求：多个子查询一次 SignalR 往返（生产复盘页多设备批查用）。
/// 服务端对每个子查询执行服务端全量翻页聚合（分页语义下 Total 收齐），
/// 客户端一次 Invoke 即拿全量，避免「逐设备 × 逐页」串行往返。
/// </summary>
public sealed record BatchHistoryQueryRequest
{
    public required IReadOnlyList<HistoryQueryRequest> Queries { get; init; }
}

/// <summary>
/// 批量历史查询响应：Results 与请求 Queries 顺序一一对应。
/// 任一子查询失败时该项 ErrorCode=QueryFailed（其余子查询结果仍有效），
/// 客户端按「任一失败即整体失败」语义处理，与单查 Strict 行为一致。
/// </summary>
public sealed record BatchHistoryQueryResponse
{
    public required IReadOnlyList<HistoryQueryResponse> Results { get; init; }
}
