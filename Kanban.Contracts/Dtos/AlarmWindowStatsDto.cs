namespace Kanban.Contracts.Dtos;

/// <summary>
/// 报警窗口服务端统计：KPI + Top N + 最近事件。
/// WASM 报警中心不再为今日计数/排行全量拉取事件（厂级窗口反序列化会冻 UI）。
/// </summary>
public sealed record AlarmWindowStatsDto
{
    public HistoryErrorCode ErrorCode { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
    public int WindowTriggered { get; init; }
    public int WindowRecovered { get; init; }
    public int TodayTriggered { get; init; }
    public int TodayRecovered { get; init; }
    public int YesterdayTriggered { get; init; }
    public IReadOnlyList<AlarmTopRowDto> Top { get; init; } = [];
    public IReadOnlyList<AlarmEventRecordDto> Recent { get; init; } = [];
    public int Pending { get; init; }
    public IReadOnlyList<AlarmChartStatDto> Chart { get; init; } = [];
    public IReadOnlyList<string> AlarmNames { get; init; } = [];
    public IReadOnlyList<string> ShiftNames { get; init; } = [];
    public string? CorrelationPattern { get; init; }
    public int CorrelationCount { get; init; }
    public IReadOnlyList<AlarmPendingItemDto> PendingTop { get; init; } = [];
}

public sealed record AlarmTopRowDto
{
    public string AlarmName { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public int TriggerCount { get; init; }
    public double TotalDurationMinutes { get; init; }
}

public sealed record AlarmChartStatDto
{
    public string AlarmName { get; init; } = "";
    public int TriggerCount { get; init; }
    public double AvgDurationMin { get; init; }
}

public sealed record AlarmPendingItemDto
{
    public string AlarmName { get; init; } = "";
    public DateTime TriggerTime { get; init; }
    public double Minutes { get; init; }
}
