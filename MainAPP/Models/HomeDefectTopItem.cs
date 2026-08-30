namespace MainAPP.Models;

/// <summary>主页缺陷 TOP 行（与 WASM Home 缺陷列表项对齐：名称 + 相对柱宽），悬停显示完整缺陷名。</summary>
public sealed class HomeDefectTopItem
{
    public required string Name { get; init; }

    public int Count { get; init; }

    /// <summary>相对最大值的柱宽比例（0–1），用于横向条形图。</summary>
    public double BarRatio { get; init; }
}