using System.Globalization;
using Kanban.Localization;

namespace Kanban.Collector.Core.Localization;

/// <summary>Core 资源的目录查询。译文只存在于生成的 <see cref="LocalizationCatalog"/>。</summary>
internal static class CoreText
{
    public static string Get(string key, CultureInfo culture, string fallback)
        => LocalizationCatalog.Get("Core", key, culture.Name) ?? fallback;
}
