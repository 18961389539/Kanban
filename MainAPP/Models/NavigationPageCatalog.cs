using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Resources;
using Material.Icons;

namespace MainAPP.Models;

public static class NavigationPageCatalog
{
    /// <summary>导航项文本走多语言资源（中文默认/英文/日文，切换语言重启生效）。</summary>
    public static IReadOnlyList<NavigationPageDefinition> All { get; } =
    [
        Page("Home", 0, Strings.Nav_Home, MaterialIconKind.ViewDashboard, Strings.Nav_Home, true),
        Page("ProductionLine", 1, Strings.Nav_ProductionLine, MaterialIconKind.Factory, Strings.Nav_ProductionLine, true),
        Page("AlarmCenter", 2, Strings.Nav_AlarmCenter, MaterialIconKind.BellAlert, Strings.Nav_AlarmCenter, true),
        Page("DeviceManager", 3, Strings.Nav_DeviceManager, MaterialIconKind.Harddisk, Strings.Nav_DeviceManager, true),
        Page("WorkOrder", 4, Strings.Nav_WorkOrder, MaterialIconKind.ClipboardListOutline, Strings.Nav_WorkOrder, true),
        Page("HistoryQuery", 5, Strings.Nav_HistoryQuery, MaterialIconKind.History, Strings.Nav_HistoryQuery, true),
        Page("Overview", 6, Strings.Nav_Overview, MaterialIconKind.ChartTimelineVariant, Strings.Nav_Overview_Tip, true),
        Page("Settings", 7, Strings.Nav_Settings, MaterialIconKind.Cog, Strings.Nav_Settings, true),
        Page("RuntimeMonitoring", 8, Strings.Nav_RuntimeMonitoring, MaterialIconKind.MonitorDashboard, Strings.Nav_RuntimeMonitoring, true),
        Page("DeviceDetail", 9, Strings.Nav_DeviceDetail, MaterialIconKind.Harddisk, Strings.Nav_DeviceDetail, false),
    ];

    public static NavigationPageDefinition Home => All[0];
    public static NavigationPageDefinition ProductionLine => All[1];
    public static NavigationPageDefinition AlarmCenter => All[2];
    public static NavigationPageDefinition DeviceManager => All[3];
    public static NavigationPageDefinition WorkOrder => All[4];
    public static NavigationPageDefinition HistoryQuery => All[5];
    public static NavigationPageDefinition Overview => All[6];
    public static NavigationPageDefinition Settings => All[7];
    public static NavigationPageDefinition RuntimeMonitoring => All[8];
    public static NavigationPageDefinition DeviceDetail => All[9];

    private static NavigationPageDefinition Page(
        string key, int index, string label, MaterialIconKind icon, string toolTip, bool showInSidebar) =>
        new()
        {
            Key = key,
            Index = index,
            ShowInSidebar = showInSidebar,
            NavItem = new NavItem
            {
                Index = index,
                Label = label,
                Icon = icon,
                AccessibleName = label,
                ToolTip = toolTip,
            },
        };
}
