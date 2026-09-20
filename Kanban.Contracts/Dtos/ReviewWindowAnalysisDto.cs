namespace Kanban.Contracts.Dtos;

/// <summary>
/// 生产复盘服务端分析：全量 SQL 留在 Collector，屏端只收 KPI / 时间线 / 排行。
/// </summary>
public sealed record ReviewWindowAnalysisDto
{
    public HistoryErrorCode ErrorCode { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }
    public double QualityRate { get; init; }
    public double RunSeconds { get; init; }
    public double AlarmSeconds { get; init; }
    public double PausedSeconds { get; init; }
    public double OfflineSeconds { get; init; }
    public int AlarmTriggered { get; init; }
    public int AlarmRecovered { get; init; }
    public int AlarmPending { get; init; }
    public string LongestAlarmName { get; init; } = "";
    public double LongestAlarmHours { get; init; }
    public string PeakHour { get; init; } = "—";
    public int PeakOk { get; init; }
    public string ValleyHour { get; init; } = "—";
    public int ValleyOk { get; init; }
    public int BaselineOutput { get; init; }
    public double BaselineQuality { get; init; }
    public double BaselineRunSeconds { get; init; }
    public double BaselineAlarmSeconds { get; init; }
    public int PreviousAlarmTriggered { get; init; }
    public int PreviousDefectCount { get; init; }
    public IReadOnlyList<ProductionChartPointDto> ChartPoints { get; init; } = [];
    public IReadOnlyList<ReviewAlarmItemDto> Alarms { get; init; } = [];
    public IReadOnlyList<ReviewTimelineSegmentDto> Timeline { get; init; } = [];
    public IReadOnlyList<ReviewDefectRowDto> Defects { get; init; } = [];
    public IReadOnlyList<ReviewShiftRowDto> Shifts { get; init; } = [];
    /// <summary>15 分钟抽样产量（健康分低产口径与峰值差分用）。</summary>
    public IReadOnlyList<ProductionLogDto> CompactLogs { get; init; } = [];
}

public sealed record ReviewAlarmItemDto
{
    public string AlarmName { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string PlcAddress { get; init; } = "";
    public int TriggerCount { get; init; }
    public double AverageIntervalMinutes { get; init; }
    public bool IsHighFrequency { get; init; }
    public int OutputBefore { get; init; }
    public int OutputAfter { get; init; }
    public string ShiftName { get; init; } = "";
    public double TotalDurationHours { get; init; }
}

public sealed record ReviewTimelineSegmentDto
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int StatusWord { get; init; }
    public int OutputDelta { get; init; }
    public int AlarmCount { get; init; }
    public bool HasNoOutput { get; init; }
}

public sealed record ReviewDefectRowDto
{
    public string DefectName { get; init; } = "";
    public string ShiftName { get; init; } = "";
    public string TimeRangeText { get; init; } = "";
    public int Count { get; init; }
    public double Share { get; init; }
}

public sealed record ReviewShiftRowDto
{
    public string ShiftName { get; init; } = "";
    public int OkCount { get; init; }
    public int NgCount { get; init; }
    public int AlarmCount { get; init; }
}
