using Kanban.Core.Services;
using Kanban.Core.Models;
using Kanban.Core.Data;
using Kanban.Core.Entities;
using MainAPP.Resources;
using Material.Icons;

namespace MainAPP.Models;

public static class NavigationPageCatalog
{
    /// <summary>导航项文本走多语言资源（中文默认/英文/日文，切换语言重启生效）。
    /// 静态初始化时只存 key（NavItem 通过 getter 按 CurrentUICulture 取值），不在这里调 i18n 资源，
    /// 避免 _host.Build() 阶段（早于 Localization.Apply）把 zh-CN 字符串缓存到 NavItem 上。
    /// RequiredRole：展示类页面（Home/ProductionLine/AlarmCenter/DeviceDetail）无角色限制；
    /// 设备管理需工程师，设置/运行监控需管理员。Viewer 模式仍由 MainWindowViewModel 额外过滤。
    /// </summary>
    public static IReadOnlyList<NavigationPageDefinition> All { get; } =
    [
        Page("Home", 0, "Nav_Home", MaterialIconKind.ViewDashboard, "Nav_Home", true),
        Page("ProductionLine", 1, "Nav_ProductionLine", MaterialIconKind.Factory, "Nav_ProductionLine", true),
        Page("AlarmCenter", 2, "Nav_AlarmCenter", MaterialIconKind.BellAlert, "Nav_AlarmCenter", true),
        Page("DeviceManager", 3, "Nav_DeviceManager", MaterialIconKind.Harddisk, "Nav_DeviceManager", true, UserRole.Engineer),
        Page("WorkOrder", 4, "Nav_WorkOrder", MaterialIconKind.ClipboardListOutline, "Nav_WorkOrder", true),
        Page("HistoryQuery", 5, "Nav_HistoryQuery", MaterialIconKind.History, "Nav_HistoryQuery", true),
        Page("Overview", 6, "Nav_Overview", MaterialIconKind.ChartTimelineVariant, "Nav_Overview_Tip", true),
        Page("Settings", 7, "Nav_Settings", MaterialIconKind.Cog, "Nav_Settings", true, UserRole.Admin),
        Page("RuntimeMonitoring", 8, "Nav_RuntimeMonitoring", MaterialIconKind.MonitorDashboard, "Nav_RuntimeMonitoring", true, UserRole.Admin),
        Page("DeviceDetail", 9, "Nav_DeviceDetail", MaterialIconKind.Harddisk, "Nav_DeviceDetail", false),
        Page("UserManager", 10, "Nav_UserManager", MaterialIconKind.AccountGroup, "Nav_UserManager", true, UserRole.Admin),
        Page("Audit", 11, "Nav_Audit", MaterialIconKind.ShieldAccount, "Nav_Audit", true, UserRole.Admin),
        Page("RecipeManager", 12, "Nav_RecipeManager", MaterialIconKind.SettingsOutline, "Nav_RecipeManager", true, UserRole.Engineer),
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
    public static NavigationPageDefinition UserManager => All[10];
    public static NavigationPageDefinition Audit => All[11];
    public static NavigationPageDefinition RecipeManager => All[12];

    private static NavigationPageDefinition Page(
        string key, int index, string labelKey, MaterialIconKind icon, string toolTipKey, bool showInSidebar,
        UserRole? requiredRole = null) =>
        new()
        {
            Key = key,
            Index = index,
            ShowInSidebar = showInSidebar,
            RequiredRole = requiredRole,
            NavItem = new NavItem
            {
                Index = index,
                LabelKey = labelKey,
                Icon = icon,
                AccessibleNameKey = labelKey,
                ToolTipKey = toolTipKey,
            },
        };
}
