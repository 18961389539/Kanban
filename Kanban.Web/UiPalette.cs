using Kanban.Contracts.Metrics;

namespace Kanban.Web;

/// <summary>
/// Web 端共享色板与百分比口径（OEE 四环/状态图/图表单源，对齐 WPF ChartPalette/Brushes）。
/// 语义约定：绿=运行/OK，红=报警/NG，黄=待机/警告，灰=离线/无数据；绿色不再用于缺陷等非运行语义。
/// </summary>
public static class UiPalette
{
    // ─── 状态语义色 ───
    public const string Run = "#34D399";
    public const string Alarm = "#F87171";
    public const string Pause = "#FBBF24";
    public const string Offline = "#9CA3AF";
    public const string Primary = "#378ADD";
    public const string QualitySeries = "#818CF8";

    // ─── OEE 四环类别色（首页/监控/历史 OEE 同序同色：OEE → 稼动率 → 性能 → 良品率） ───
    public const string Oee = Run;
    public const string Availability = Primary;
    public const string Performance = "#BA7517";
    public const string Quality = "#639922";

    // 与 WPF KpiThresholds 同源：>=0.85 绿、>=0.60 黄、否则红。
    public const double OeeGood = 0.85;
    public const double OeeWarning = 0.60;

    /// <summary>OEE 百分比统一口径：1 位小数（对齐 WPF 的 P1；此前 Web 各页 P0/P1/P2 混用）。</summary>
    public static string Pct(double v) => v.ToString("P1", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>OEE 环显示文本：无有效窗口时显示 "—"，避免与真实 0% 混淆。</summary>
    public static string RingText(double v, bool hasData) => hasData ? Pct(v) : "—";

    /// <summary>产线卡 OEE 数值着色类（对齐 WPF OeeThresholdConverter）。</summary>
    public static string OeeToneClass(double value)
        => value >= OeeGood ? "oee-ok" : value >= OeeWarning ? "oee-watch" : "oee-low";

    /// <summary>班次累计窗口是否有活动时长（运行+报警+待机）。无窗口时环图显示 "—"。</summary>
    public static bool HasOeeWindow(double runTime, double alarmTime, double pausedTime)
        => SnapshotMetrics.OeeActiveTimeSeconds(runTime, alarmTime, pausedTime) > 0;
}
