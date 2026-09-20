namespace Kanban.Contracts.Dtos;

/// <summary>
/// 产量窗口服务端分析：KPI + 15 分钟抽样点。
/// WASM 不再为图表/合格率全量拉取产量快照（数万条反序列化会冻 UI）。
/// </summary>
public sealed record ProductionWindowAnalysisDto
{
    public HistoryErrorCode ErrorCode { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }
    public double QualityRate { get; init; }
    public IReadOnlyList<ProductionChartPointDto> ChartPoints { get; init; } = [];
    /// <summary>15 分钟抽样日志（含班次名），供分班次 OEE 使用。</summary>
    public IReadOnlyList<ProductionLogDto> CompactLogs { get; init; } = [];
}

public sealed record ProductionChartPointDto
{
    public DateTime Time { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }
}
