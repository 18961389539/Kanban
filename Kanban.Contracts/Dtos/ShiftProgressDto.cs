namespace Kanban.Contracts.Dtos;

/// <summary>
/// 班次进度（Collector 按当前时间与班次配置计算，客户端只渲染）。
/// 口径与 MainAPP HomeViewModel.UpdateShiftProgress 一致。
/// </summary>
public sealed record ShiftProgressDto
{
    /// <summary>当前是否处于某个班次时段内。</summary>
    public bool IsInShift { get; init; }

    /// <summary>当前班次名；非班次时段为"非班次时段"。</summary>
    public string Name { get; init; } = "";

    /// <summary>已运行时间文本（如 "3h 25m" / "45m"）。</summary>
    public string ElapsedText { get; init; } = "";

    /// <summary>剩余时间文本（如 "1h 35m"）。</summary>
    public string RemainingText { get; init; } = "";

    /// <summary>班次进度比例（0.0-1.0）。</summary>
    public double Ratio { get; init; }

    /// <summary>进度百分比文本（如 "70%"）。</summary>
    public string Pct { get; init; } = "";
}
