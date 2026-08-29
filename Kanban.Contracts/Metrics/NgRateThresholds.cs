namespace Kanban.Contracts.Metrics;

/// <summary>
/// 不良率阈值（低=好）单源：主页实时着色、复盘分桶异常标记共用此类，禁止借用达成率阈值。
/// </summary>
public static class NgRateThresholds
{
    /// <summary>达到则 Warning（主页黄色警示）。</summary>
    public const double Warning = 0.03;

    /// <summary>达到则 Danger（主页红色警示）。</summary>
    public const double Danger = 0.05;

    /// <summary>
    /// 复盘趋势图分桶异常标记阈值：单桶 NG 率超过此值时柱顶打「!」。
    /// 高于 <see cref="Danger"/>，避免小产量桶在 5% 附近频繁误报；与实时 KPI 着色口径分离。
    /// </summary>
    public const double BucketAlert = 0.10;
}
