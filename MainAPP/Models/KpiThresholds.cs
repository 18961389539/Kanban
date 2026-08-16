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

    // 良品率阈值（2026-08-11 方案 E）：复盘页结论/状态文案已改为引用本常量（2026-08-16 唯一源），
    // 良品率卡片的状态 chip 与数字着色统一使用，避免 OEE 阈值(0.85)误用于良品率导致
    // "92% 数字绿但未达 95% 目标"的矛盾观感。
    public const double QualityGood = 0.95;
}
