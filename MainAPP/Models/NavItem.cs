using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using Material.Icons;
using Material.Icons.WPF;

namespace MainAPP.Models;

/// <summary>
/// 侧边栏导航项数据模型。
/// 用于 ListBox + ListBoxItem 模板的数据绑定，替代 hc:SideMenuItem，
/// 使 AutomationProperties.Name 能正确暴露到 UIA 树（标准 ListBoxItem 原生支持 UIA）。
/// </summary>
public sealed class NavItem
{
    /// <summary>页面索引（与 MainWindowViewModel.SelectedIndex 对应）</summary>
    public int Index { get; init; }

    /// <summary>显示文本（折叠时隐藏，仅显示图标）</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Material 图标类型</summary>
    public MaterialIconKind Icon { get; init; }

    /// <summary>UIA 可访问名称（与 Label 一致，但独立于折叠可见性，始终暴露）</summary>
    public string AccessibleName { get; init; } = string.Empty;

    /// <summary>悬停提示</summary>
    public string ToolTip { get; init; } = string.Empty;
}
