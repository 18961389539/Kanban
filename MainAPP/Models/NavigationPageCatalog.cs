using Material.Icons;

namespace MainAPP.Models;

public static class NavigationPageCatalog
{
    public static IReadOnlyList<NavigationPageDefinition> All { get; } =
    [
        Page("Home", 0, "主页", MaterialIconKind.ViewDashboard, "主页", true),
        Page("ProductionLine", 1, "产线总览", MaterialIconKind.Factory, "产线总览", true),
        Page("AlarmCenter", 2, "报警中心", MaterialIconKind.BellAlert, "报警中心", true),
        Page("DeviceManager", 3, "设备管理", MaterialIconKind.Harddisk, "设备管理", true),
        Page("WorkOrder", 4, "工单管理", MaterialIconKind.ClipboardListOutline, "工单管理", true),
        Page("HistoryQuery", 5, "历史查询", MaterialIconKind.History, "历史查询", true),
        Page("Overview", 6, "生产复盘", MaterialIconKind.ChartTimelineVariant, "最近24小时生产复盘", true),
        Page("Settings", 7, "设置", MaterialIconKind.Cog, "设置", true),
        Page("RuntimeMonitoring", 8, "运行监控", MaterialIconKind.MonitorDashboard, "运行状态监控", true),
        Page("DeviceDetail", 9, "设备详情", MaterialIconKind.Harddisk, "设备详情", false),
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
