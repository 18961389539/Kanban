using System.Globalization;
using System.Resources;

namespace Kanban.Collector.Core.Localization;

/// <summary>
/// 进程间共享的连接状态文案（被 Kanban.Collector 和 MainAPP 共同依赖的 Kanban.Collector.Core 使用）。
/// 默认值是中文以保留向后兼容；MainAPP 启动时根据用户界面语言整体覆盖一次（Collected/MainAPP 都生效）。
    /// en/ja/pt-BR 文案从 Resources/Messages.{en,ja,pt-BR}.resx 卫星程序集读取；
    /// 这些 RESX 由统一 Localization.csv 在编译前生成。
/// </summary>
public static class ConnectionStatusMessages
{
    private static readonly ResourceManager s_rm = new(
        "Kanban.Collector.Core.Resources.Messages",
        typeof(ConnectionStatusMessages).Assembly);

    /// <summary>默认（zh-CN）。</summary>
    public const string DefaultConnected = "已连接";
    public const string DefaultDisconnected = "未连接";
    public const string DefaultConnectionLost = "连接断开";
    public const string DefaultDisconnectedWithRetry = "未连接 (重试间隔{0:F0}s)";
    public const string DefaultConnectingPrefix = "正在连接";
    public const string DefaultConnectingSuffix = "... (第{0}次)";
    public const string DefaultRemoteConnecting = "采集服务 (第{0}次)";

    private static string s_connected = DefaultConnected;
    private static string s_disconnected = DefaultDisconnected;
    private static string s_connectionLost = DefaultConnectionLost;
    private static string s_disconnectedWithRetry = DefaultDisconnectedWithRetry;
    private static string s_connectingPrefix = DefaultConnectingPrefix;
    private static string s_connectingSuffix = DefaultConnectingSuffix;
    private static string s_remoteConnecting = DefaultRemoteConnecting;

    public static string Connected => s_connected;
    public static string Disconnected => s_disconnected;
    public static string ConnectionLost => s_connectionLost;
    public static string DisconnectedWithRetry => s_disconnectedWithRetry;
    public static string ConnectingPrefix => s_connectingPrefix;
    public static string ConnectingSuffix => s_connectingSuffix;
    public static string RemoteConnecting => s_remoteConnecting;

    /// <summary>
    /// 一次性格式化覆盖全部文案。MainAPP 启动时根据当前用户语言设置一遍；
    /// Collected（Kanban.Collector 服务进程）通过 KANBAN_HARDWARE_LANG env var 接收 MainAPP 推送的语言，
    /// 并在 CollectorWorker.InitializeAsync 中同样调用本方法。
    /// 留空参数表示还原中文默认值。
    /// </summary>
    public static void Override(
        string? connected = null,
        string? disconnected = null,
        string? connectionLost = null,
        string? disconnectedWithRetry = null,
        string? connectingPrefix = null,
        string? connectingSuffix = null,
        string? remoteConnecting = null)
    {
        s_connected = connected ?? DefaultConnected;
        s_disconnected = disconnected ?? DefaultDisconnected;
        s_connectionLost = connectionLost ?? DefaultConnectionLost;
        s_disconnectedWithRetry = disconnectedWithRetry ?? DefaultDisconnectedWithRetry;
        s_connectingPrefix = connectingPrefix ?? DefaultConnectingPrefix;
        s_connectingSuffix = connectingSuffix ?? DefaultConnectingSuffix;
        s_remoteConnecting = remoteConnecting ?? DefaultRemoteConnecting;
    }

    /// <summary>
    /// 简单语言预设：根据语言文化代码覆盖全部文案。传入 null 还原默认中文。
    /// 非中文文案从生成的 Messages.{en,ja,pt-BR}.resx 卫星程序集读取，避免硬编码副本。
    /// </summary>
    public static void ApplyLanguage(string? langCode)
    {
        if (langCode is null)
        {
            Override(null, null, null, null, null, null, null);
            return;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(langCode);
        }
        catch (CultureNotFoundException)
        {
            Override(null, null, null, null, null, null, null);
            return;
        }

        Override(
            connected: s_rm.GetString("Conn_Connected", culture),
            disconnected: s_rm.GetString("Conn_Disconnected", culture),
            connectionLost: s_rm.GetString("Conn_ConnectionLost", culture),
            disconnectedWithRetry: s_rm.GetString("Conn_DisconnectedWithRetry", culture),
            connectingPrefix: s_rm.GetString("Conn_ConnectingPrefix", culture),
            connectingSuffix: s_rm.GetString("Conn_ConnectingSuffix", culture),
            remoteConnecting: s_rm.GetString("Conn_RemoteConnecting", culture));
        ApplyExternalOverrides(culture);
    }

    private static void ApplyExternalOverrides(CultureInfo culture)
    {
        if (LocalizationOverrideStore.TryGet("Core", "Conn_Connected", culture, out var connected))
            s_connected = connected;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_Disconnected", culture, out var disconnected))
            s_disconnected = disconnected;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_ConnectionLost", culture, out var connectionLost))
            s_connectionLost = connectionLost;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_DisconnectedWithRetry", culture, out var disconnectedWithRetry))
            s_disconnectedWithRetry = disconnectedWithRetry;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_ConnectingPrefix", culture, out var connectingPrefix))
            s_connectingPrefix = connectingPrefix;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_ConnectingSuffix", culture, out var connectingSuffix))
            s_connectingSuffix = connectingSuffix;
        if (LocalizationOverrideStore.TryGet("Core", "Conn_RemoteConnecting", culture, out var remoteConnecting))
            s_remoteConnecting = remoteConnecting;
    }
}
