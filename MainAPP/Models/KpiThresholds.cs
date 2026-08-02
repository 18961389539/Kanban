namespace MainAPP.Models;

/// <summary>
/// KPI 阈值统一管理。Converter、ChartService、ViewModel 共用此常量类。
/// </summary>
public static class KpiThresholds
{
    // OEE 阈值
    public const double OeeGood = 0.85;
    public const double OeeWarning = 0.60;

    // 达成率阈值
    public const double AchievementGood = 0.90;
    public const double AchievementWarning = 0.70;
}
