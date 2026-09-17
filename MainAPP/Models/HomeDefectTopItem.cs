using Kanban.Collector.Core.Models;

namespace MainAPP.Models;

/// <summary>主页缺陷帕累托行：名次 + 名称 + 相对合计的柱宽 + 累计占比。</summary>
public sealed class HomeDefectTopItem
{
    public int Rank { get; init; }

    public string RankText { get; init; } = "";

    public required string Name { get; init; }

    public int Count { get; init; }

    /// <summary>相对缺陷合计的柱宽（0–1）。</summary>
    public double BarRatio { get; init; }

    /// <summary>从第一名累计到本行的占比（0–1），折线用此值。</summary>
    public double CumulativeShare { get; init; }

    public string ShareText { get; init; } = "";

    public string CumulativeText { get; init; } = "";

    public bool IsVitalFew { get; init; }

    public bool IsOthers { get; init; }

    public DefectSeverity Severity { get; init; }

    public DefectCategory Category { get; init; }

    public string Tooltip { get; init; } = "";
}
