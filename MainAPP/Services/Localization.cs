using System.Globalization;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 界面语言应用服务：把 CSV 中的语言文化代码映射为 CultureInfo 并应用到当前进程。
/// 在 App 启动（MainWindow 渲染前）调用一次；切换语言由设置页持久化 AppSettings.LanguageCode，
/// 重启后本服务重新应用（resx 卫星程序集按 CurrentUICulture 自动选择）。
/// </summary>
public static class Localization
{
    /// <summary>兼容旧枚举的语言映射；新语言直接传入 CSV 中的文化代码。</summary>
    public static string GetCultureName(AppLanguage language) => language switch
    {
        AppLanguage.En => "en-US",
        AppLanguage.Ja => "ja-JP",
        AppLanguage.PtBr => "pt-BR",
        _ => "zh-CN",
    };

    public static IReadOnlyList<string> LanguageCodes => LocalizationCatalog.LanguageCodes;

    public static string Normalize(string? languageCode)
        => LocalizationCatalog.Normalize(languageCode);

    public static string GetCultureName(string languageCode)
        => LocalizationCatalog.Normalize(languageCode);

    /// <summary>
    /// 启动时 <see cref="Apply"/> 捕获的语言代码。WPF UI 线程的 CurrentUICulture 可能仍是系统默认中文
    /// （Dispatcher 在 Apply 之前创建），因此报警名等多语言解析必须读此快照，不能读 CurrentUICulture。
    /// </summary>
    private static string s_appliedLanguageCode = LocalizationCatalog.DefaultLanguage;

    /// <summary>把指定语言应用到当前进程（UI 文化与数字/日期格式文化同步切换）。同时把 culture 快照写入 Strings 静态字段，避免运行时 CurrentUICulture 不一致导致 UI 出现混合语言。</summary>
    public static void Apply(AppLanguage language)
        => Apply(GetCultureName(language));

    public static void Apply(string languageCode)
    {
        var culture = new CultureInfo(GetCultureName(languageCode));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        s_appliedLanguageCode = culture.Name;
        MainAPP.Resources.Strings.CaptureCulture(culture);
    }

    /// <summary>当前界面语言代码（与资源字符串同源，不依赖线程 CurrentUICulture）。</summary>
    public static string CurrentLanguageCode => s_appliedLanguageCode;
}
