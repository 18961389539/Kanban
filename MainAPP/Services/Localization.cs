using System.Globalization;
using Kanban.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 界面语言应用服务：把 <see cref="AppLanguage"/> 映射为 CultureInfo 并应用到当前进程。
/// 在 App 启动（MainWindow 渲染前）调用一次；切换语言由设置页持久化 AppSettings.Language，
/// 重启后本服务重新应用（resx 卫星程序集按 CurrentUICulture 自动选择）。
/// </summary>
public static class Localization
{
    /// <summary>语言 → CultureInfo 名称（resx 卫星按 en/ja 回退，中文走中性资源）。</summary>
    public static string GetCultureName(AppLanguage language) => language switch
    {
        AppLanguage.En => "en-US",
        AppLanguage.Ja => "ja-JP",
        _ => "zh-CN",
    };

    /// <summary>把指定语言应用到当前进程（UI 文化与数字/日期格式文化同步切换）。</summary>
    public static void Apply(AppLanguage language)
    {
        var culture = new CultureInfo(GetCultureName(language));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
