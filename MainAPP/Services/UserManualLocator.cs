using System.IO;
using Kanban.Collector.Core.Localization;
using Kanban.Collector.Core.Services;

namespace MainAPP.Services;

/// <summary>
/// 定位随界面语言选择的内置使用手册。中文及其他非英文界面用中文手册，英文界面用英文手册。
/// </summary>
public static class UserManualLocator
{
    public static string ExpectedFileName(AppSettings appSettings)
        => IsEnglish(appSettings) ? "MainAPP_User_Manual_EN.md" : "MainAPP用户使用手册.md";

    public static string? Resolve(AppSettings appSettings)
    {
        var fileName = ExpectedFileName(appSettings);
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { "手册", "Help", "Manual" })
        {
            var candidate = Path.Combine(baseDir, dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        var current = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            var devCandidate = Path.Combine(current.FullName, "MainAPP", "手册", fileName);
            if (File.Exists(devCandidate))
                return devCandidate;
            current = current.Parent;
        }

        return null;
    }

    private static bool IsEnglish(AppSettings appSettings)
        => LocalizationCatalog.Normalize(appSettings.EffectiveLanguageCode)
            .StartsWith("en", StringComparison.OrdinalIgnoreCase);
}
