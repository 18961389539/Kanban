namespace Kanban.Contracts.Metrics;

/// <summary>
/// 良品率阈值（高=好）单源：WPF 与 Web 的主页、工单、复盘着色及结论共用。
/// </summary>
public static class QualityThresholds
{
    /// <summary>达到则达标（绿）。</summary>
    public const double Good = 0.95;

    /// <summary>达到则接近目标（琥珀），否则红。</summary>
    public const double Warning = 0.90;
}
