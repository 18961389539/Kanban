using System.Globalization;
using System.Resources;

namespace MainAPP.Resources;

/// <summary>
/// 多语言资源强类型访问（中文默认 / 英文 / 日文）。
/// 对应 resx：Strings.resx（中文中性资源）+ Strings.en.resx + Strings.ja.resx，
/// ResourceManager 按 CurrentUICulture 自动选择卫星资源。
/// key 集一致性由测试 LocalizationResourceConsistencyTests 兜底（zh/en/ja 三语 key 必须相同）。
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Res = new("MainAPP.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>按当前 UI 文化取资源；缺失时回退默认值（中文），避免界面出现空白。</summary>
    private static string S(string key, string fallback)
        => Res.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;

    public static string Nav_Home => S("Nav_Home", "主页");
    public static string Nav_ProductionLine => S("Nav_ProductionLine", "产线总览");
    public static string Nav_AlarmCenter => S("Nav_AlarmCenter", "报警中心");
    public static string Nav_DeviceManager => S("Nav_DeviceManager", "设备管理");
    public static string Nav_WorkOrder => S("Nav_WorkOrder", "工单管理");
    public static string Nav_HistoryQuery => S("Nav_HistoryQuery", "历史查询");
    public static string Nav_Overview => S("Nav_Overview", "生产复盘");
    public static string Nav_Settings => S("Nav_Settings", "设置");
    public static string Nav_RuntimeMonitoring => S("Nav_RuntimeMonitoring", "运行监控");
    public static string Nav_DeviceDetail => S("Nav_DeviceDetail", "设备详情");
    public static string Nav_Overview_Tip => S("Nav_Overview_Tip", "最近24小时生产复盘");

    public static string Status_Initial => S("Status_Initial", "初始");
    public static string Status_Running => S("Status_Running", "运行");
    public static string Status_Alarm => S("Status_Alarm", "报警");
    public static string Status_Paused => S("Status_Paused", "待机");
    public static string Status_Unknown => S("Status_Unknown", "未知");

    public static string Common_Save => S("Common_Save", "保存设置");
    public static string Common_Cancel => S("Common_Cancel", "取消");
    public static string Common_Language => S("Common_Language", "语言");
    public static string Common_RestartRequired => S("Common_RestartRequired", "语言切换将在重启后生效。");

    public static string Settings_DisplaySettings => S("Settings_DisplaySettings", "显示设置");
    public static string Settings_DisplayHint => S("Settings_DisplayHint", "调整看板标题与界面字号（大屏远距阅读）。");
    public static string Settings_LanguageHint => S("Settings_LanguageHint", "界面语言（中文/英文/日文），默认中文，重启后生效。");

    public static string Conn_Live => S("Conn_Live", "实时");
    public static string Conn_Disconnected => S("Conn_Disconnected", "未连接");
    public static string Conn_Reconnecting => S("Conn_Reconnecting", "重连中");
    public static string Conn_Stale => S("Conn_Stale", "数据停滞");

    public static string EventType_Triggered => S("EventType_Triggered", "触发");
    public static string EventType_Recovered => S("EventType_Recovered", "恢复");
    public static string EventType_ShiftChange => S("EventType_ShiftChange", "班次切换");

    public static string Level_High => S("Level_High", "高");
    public static string Level_Medium => S("Level_Medium", "中");
    public static string Level_Low => S("Level_Low", "低");

    public static string Defect_Appearance => S("Defect_Appearance", "外观");
    public static string Defect_Dimension => S("Defect_Dimension", "尺寸");
    public static string Defect_Function => S("Defect_Function", "功能");
    public static string Defect_Packaging => S("Defect_Packaging", "包装");
    public static string Defect_Other => S("Defect_Other", "其他");

    public static string Severity_Critical => S("Severity_Critical", "严重");
    public static string Severity_Major => S("Severity_Major", "一般");
    public static string Severity_Minor => S("Severity_Minor", "轻微");
}
