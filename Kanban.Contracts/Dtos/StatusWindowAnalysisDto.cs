namespace Kanban.Contracts.Dtos;

/// <summary>
/// 状态窗口服务端分析：时长 / 按天 / 甘特段。屏端不再为 KPI 与图表全量拉取转换事件。
/// </summary>
public sealed record StatusWindowAnalysisDto
{
    public HistoryErrorCode ErrorCode { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
    public int InitialState { get; init; } = 1;
    public int TotalCount { get; init; }
    public double RunSeconds { get; init; }
    public double AlarmSeconds { get; init; }
    public double PausedSeconds { get; init; }
    public double OfflineSeconds { get; init; }
    public IReadOnlyList<StatusDailyDurationDto> Daily { get; init; } = [];
    public IReadOnlyList<StatusSegmentDto> Segments { get; init; } = [];
    public IReadOnlyList<string> ShiftNames { get; init; } = [];
}

public sealed record StatusDailyDurationDto
{
    public DateTime Date { get; init; }
    public double RunHours { get; init; }
    public double AlarmHours { get; init; }
    public double PauseHours { get; init; }
}

public sealed record StatusSegmentDto
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int State { get; init; }
}
