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

    // ─── OEE 四环类别色（首页/监控/产线/历史 OEE 同序同色：OEE → 稼动率 → 性能 → 良品率） ───
    public const string Oee = Run;
    public const string Availability = "#378ADD";
    public const string Performance = "#BA7517";
    public const string Quality = "#639922";

    /// <summary>OEE 百分比统一口径：1 位小数（对齐 WPF 的 P1；此前 Web 各页 P0/P1/P2 混用）。</summary>
    public static string Pct(double v) => v.ToString("P1", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>OEE 环显示文本：无在制工单时显示 "—"（等待投产语义），避免与真实 0% 混淆。</summary>
    public static string RingText(double v, bool hasWorkOrder) => hasWorkOrder ? Pct(v) : "—";
}
