using Kanban.Collector.Core.Services;
using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Data;
using Kanban.Collector.Core.Entities;
using MainAPP.Resources;
using Material.Icons;

namespace MainAPP.Models;

public static class NavigationPageCatalog
{
    /// <summary>导航项文本走多语言资源（中文默认/英文/日文，切换语言重启生效）。
    /// 静态初始化时只存 key（NavItem 通过 getter 按 CurrentUICulture 取值），不在这里调 i18n 资源，
    /// 避免 _host.Build() 阶段（早于 Localization.Apply）把 zh-CN 字符串缓存到 NavItem 上。
    /// RequiredRole：展示类页面（Home/ProductionLine/AlarmCenter/DeviceDetail/DataSourceMonitoring）无角色限制；
    /// 设备管理/配方管理需工程师，设置/运行监控/用户管理/审计需管理员。Viewer 模式仍由 MainWindowViewModel 额外过滤。
    ///
    /// 顺序原则（2026-09-05 调整）：按「使用频率 + 同类聚合」排列，高频在前、低频在后——
    ///   0-3  高频区：监控与作业（日常盯屏与操作主路径，无角色限制）
    ///   4-5  中频区：复盘与查询分析
    ///   6-7  低频区：设备与配方配置（工程师）
    ///   8-11 系统管理区（管理员）
    ///   12   诊断区：数据监控（采集健康诊断，无角色限制但属低频，放最后）
    ///   13   上下文页 DeviceDetail：由主页「查看详情」进入，不出现在侧边栏，放最后以免占用快捷键位
    /// 侧边栏按本集合顺序渲染并再按角色过滤，因此快捷键 Ctrl+1~9 必须映射到「过滤后的可见位置」
    /// 而非本 Index（见 MainWindowViewModel.SelectPage）。
    /// </summary>
    public static IReadOnlyList<NavigationPageDefinition> All { get; } =
    [
        // —— 高频区：监控与作业（所有角色可见）——
        Page("Home", 0, "Nav_Home", MaterialIconKind.ViewDashboard, "Nav_Home", true),
        Page("ProductionLine", 1, "Nav_ProductionLine", MaterialIconKind.Factory, "Nav_ProductionLine", true),
        Page("AlarmCenter", 2, "Nav_AlarmCenter", MaterialIconKind.BellAlert, "Nav_AlarmCenter", true),
        Page("WorkOrder", 3, "Nav_WorkOrder", MaterialIconKind.ClipboardListOutline, "Nav_WorkOrder", true),
        // —— 中频区：复盘与查询分析 ——
        Page("Overview", 4, "Nav_Overview", MaterialIconKind.ChartTimelineVariant, "Nav_Overview_Tip", true),
        Page("HistoryQuery", 5, "Nav_HistoryQuery", MaterialIconKind.History, "Nav_HistoryQuery", true),
        // —— 低频区：设备与配方配置（工程师）——
        Page("DeviceManager", 6, "Nav_DeviceManager", MaterialIconKind.Harddisk, "Nav_DeviceManager", true, UserRole.Engineer),
        Page("RecipeManager", 7, "Nav_RecipeManager", MaterialIconKind.SettingsOutline, "Nav_RecipeManager", true, UserRole.Engineer),
        // —— 系统管理区（管理员）——
        Page("Settings", 8, "Nav_Settings", MaterialIconKind.Cog, "Nav_Settings", true, UserRole.Admin),
        Page("RuntimeMonitoring", 9, "Nav_RuntimeMonitoring", MaterialIconKind.MonitorDashboard, "Nav_RuntimeMonitoring", true, UserRole.Admin),
        Page("UserManager", 10, "Nav_UserManager", MaterialIconKind.AccountGroup, "Nav_UserManager", true, UserRole.Admin),
        Page("Audit", 11, "Nav_Audit", MaterialIconKind.ShieldAccount, "Nav_Audit", true, UserRole.Admin),
        // —— 诊断区（低频，放最后）——
        Page("DataSourceMonitoring", 12, "Nav_DataSourceMonitoring", MaterialIconKind.ChartTimelineVariant, "Nav_DataSourceMonitoring", true),
        // —— 上下文页：不进侧边栏 ——
        Page("DeviceDetail", 13, "Nav_DeviceDetail", MaterialIconKind.Harddisk, "Nav_DeviceDetail", false),
    ];

    public static NavigationPageDefinition Home => All[0];
    public static NavigationPageDefinition ProductionLine => All[1];
    public static NavigationPageDefinition AlarmCenter => All[2];
    public static NavigationPageDefinition WorkOrder => All[3];
    public static NavigationPageDefinition Overview => All[4];
    public static NavigationPageDefinition HistoryQuery => All[5];
    public static NavigationPageDefinition DeviceManager => All[6];
    public static NavigationPageDefinition RecipeManager => All[7];
    public static NavigationPageDefinition Settings => All[8];
    public static NavigationPageDefinition RuntimeMonitoring => All[9];
    public static NavigationPageDefinition UserManager => All[10];
    public static NavigationPageDefinition Audit => All[11];
    public static NavigationPageDefinition DataSourceMonitoring => All[12];
    public static NavigationPageDefinition DeviceDetail => All[13];

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
