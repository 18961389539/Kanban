using System.Windows;

namespace MainAPP;

/// <summary>
/// 全局字号缩放管理：把 <see cref="Styles.FontSizes.xaml"/> 中定义的语义字号资源，
/// 按 base × scale 覆盖到 Application.Resources，供所有 DynamicResource 引用者实时生效。
/// 用于“大屏远距可读性”场景：标准 100% / 大屏 115% / 超大屏 130%。
/// </summary>
public static class FontSizeManager
{
    // 基础字号（与 FontSizes.xaml 初始值保持一致，作为缩放的唯一基准）
    private static readonly Dictionary<string, double> BaseSizes = new()
    {
        ["FontSizeHero"] = 48,
        ["FontSizeKpiLg"] = 32,
        ["FontSizeKpiMd"] = 26,
        ["FontSizeKpiSm"] = 20,
        ["FontSizeKpiXs"] = 18,
        ["FontSizeKpiLabel"] = 15,
        ["FontSizeCaption"] = 13,
        ["FontSizeHint"] = 13,
        ["FontSizeMicro"] = 12,
        ["FontSizePageTitle"] = 28,
        ["FontSizeSectionTitle"] = 19,
        ["FontSizeSubtitle"] = 16,
    };

    /// <summary>
    /// 按 scale 应用全局字号。scale 一般取 1.0 / 1.15 / 1.3。
    /// 每次均基于 BaseSizes 计算，重复调用不会叠加放大。
    /// </summary>
    public static void ApplyScale(double scale)
    {
        if (Application.Current is null) return;
        var resources = Application.Current.Resources;
        foreach (var (key, baseSize) in BaseSizes)
        {
            resources[key] = Math.Round(baseSize * scale, 1);
        }
    }
}
