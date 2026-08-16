using System.Globalization;
using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;
using Material.Icons;
using Material.Icons.WPF;

namespace MainAPP.Models;

/// <summary>
/// 侧边栏导航项数据模型。
/// 用于 ListBox + ListBoxItem 模板的数据绑定，替代 hc:SideMenuItem，
/// 使 AutomationProperties.Name 能正确暴露到 UIA 树（标准 ListBoxItem 原生支持 UIA）。
///
/// **多语言**：Label/AccessibleName/ToolTip 改为 key 字段 + 计算 getter，
/// 避免静态初始化时把 zh-CN 字符串缓存到 NavItem 上（彼时 Localization.Apply 还没执行）。
/// 字段：`LabelKey` / `AccessibleNameKey` / `ToolTipKey`；属性 getter 每次读 `Strings.S(key)`，
/// 使用 `CultureInfo.CurrentUICulture`——保证了"重启后语言"语义：XAML 加载时 Locale 已应用，
/// 第一次 binding 拿到当前 Locale 的字符串。
/// </summary>
public sealed class NavItem
{
    /// <summary>页面索引（与 MainWindowViewModel.SelectedIndex 对应）</summary>
    public int Index { get; init; }

    /// <summary>显示文本（折叠时隐藏，仅显示图标）。从 LabelKey 计算 fallback 时回退 key 本身。</summary>
    public string Label => string.IsNullOrEmpty(LabelKey) ? string.Empty : Strings.S(LabelKey, LabelKey);

    /// <summary>本地化 key（多语言资源 key，如 "Nav_Home"）。运行时通过 Label getter 按 CurrentUICulture 取值。</summary>
    public string LabelKey { get; init; } = string.Empty;

    /// <summary>Material 图标类型</summary>
    public MaterialIconKind Icon { get; init; }

    /// <summary>UIA 可访问名称（与 Label 一致，但独立于折叠可见性，始终暴露）</summary>
    public string AccessibleName => string.IsNullOrEmpty(AccessibleNameKey) ? string.Empty : Strings.S(AccessibleNameKey, AccessibleNameKey);

    /// <summary>可访问名称本地化 key。</summary>
    public string AccessibleNameKey { get; init; } = string.Empty;

    /// <summary>悬停提示</summary>
    public string ToolTip => string.IsNullOrEmpty(ToolTipKey) ? string.Empty : Strings.S(ToolTipKey, ToolTipKey);

    /// <summary>悬停提示本地化 key。</summary>
    public string ToolTipKey { get; init; } = string.Empty;
}
