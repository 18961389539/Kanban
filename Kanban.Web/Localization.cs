using System.Globalization;
using Kanban.Contracts.Dtos;
using Kanban.Localization;

namespace Kanban.Web;

/// <summary>屏端语言代码常量。运行时语言列表以 Localization.csv 表头为准。</summary>
public static class WebLanguage
{
    public const string zh_CN = "zh-CN";
    public const string en_US = "en-US";
    public const string ja_JP = "ja-JP";
    public const string pt_BR = "pt-BR";
}

/// <summary>
/// 屏端文案入口。译文来自共享 <see cref="LocalizationCatalog"/>，这里只保留当前语言和启动覆盖。
/// </summary>
public static class L
{
    private static readonly string[] LegacyLanguageCodes = ["zh-CN", "en-US", "ja-JP", "pt-BR"];
    private static Dictionary<string, string> s_overrides = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> LanguageCodes => LocalizationCatalog.LanguageCodes;
    public const string DefaultLanguage = LocalizationCatalog.DefaultLanguage;
    public static string Current { get; set; } = DefaultLanguage;

    public static bool IsSupportedLanguage(string? language)
        => LocalizationCatalog.IsSupported(language);

    public static string NormalizeLanguage(string? language)
        => LocalizationCatalog.Normalize(language);

    public static string FromLegacyIndex(int index)
    {
        if (index < 0 || index >= LegacyLanguageCodes.Length)
            return DefaultLanguage;
        return IsSupportedLanguage(LegacyLanguageCodes[index])
            ? NormalizeLanguage(LegacyLanguageCodes[index])
            : DefaultLanguage;
    }

    /// <summary>合并 Collector 下发的 WPF 文案覆盖。未知 Key 或占位符不一致的值会被忽略。</summary>
    public static void ApplyOverrides(IEnumerable<LocalizationOverrideDto> overrides)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in overrides)
        {
            if (!string.Equals(item.Resource, "Wpf", StringComparison.Ordinal))
                continue;

            var culture = NormalizeLanguage(item.CultureName);
            if (!IsSupportedLanguage(culture))
                continue;

            var key = item.Key.StartsWith("Web_", StringComparison.Ordinal)
                ? item.Key[4..]
                : item.Key;
            var builtin = Builtin(key, culture);
            if (builtin is null || !HasCompatibleFormat(builtin, item.Value))
                continue;
            values[culture + "|" + key] = item.Value;
        }

        Interlocked.Exchange(ref s_overrides, values);
    }

    public static string T(string key, params object[] args)
    {
        var text = Volatile.Read(ref s_overrides).TryGetValue(NormalizeLanguage(Current) + "|" + key, out var overridden)
            ? overridden
            : Builtin(key, NormalizeLanguage(Current)) ?? key;
        if (args.Length == 0)
            return text;
        try
        {
            return string.Format(text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>指标说明：把资源里的 \n 换成真实换行，供 title / tooltip 使用。</summary>
    public static string Tip(string key, params object[] args)
        => T(key, args).Replace("\\n", "\n", StringComparison.Ordinal);

    private static string? Builtin(string key, string language)
    {
        if (LocalizationCatalog.IsKnownKey("Wpf", "Web_" + key))
            return LocalizationCatalog.Get("Wpf", "Web_" + key, language);
        return LocalizationCatalog.Get("Wpf", key, language);
    }

    private static bool HasCompatibleFormat(string expected, string actual)
    {
        if (!TryGetPlaceholderSignature(expected, out var expectedSignature)
            || !TryGetPlaceholderSignature(actual, out var actualSignature)
            || !expectedSignature.OrderBy(pair => pair.Key)
                .SequenceEqual(actualSignature.OrderBy(pair => pair.Key)))
            return false;

        try
        {
            var maxIndex = actualSignature.Keys.DefaultIfEmpty(-1).Max();
            var arguments = Enumerable.Range(0, maxIndex + 1)
                .Select(_ => (object)new FormatProbe())
                .ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, actual, arguments);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetPlaceholderSignature(string value, out Dictionary<int, int> signature)
    {
        signature = new Dictionary<int, int>();
        var maxIndex = -1;
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '{')
            {
                if (index + 1 < value.Length && value[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                index++;
                var start = index;
                while (index < value.Length && char.IsDigit(value[index])) index++;
                if (start == index || !int.TryParse(value[start..index], out var argumentIndex))
                    return false;
                maxIndex = Math.Max(maxIndex, argumentIndex);

                if (index < value.Length && value[index] == ',')
                {
                    index++;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                    if (index < value.Length && value[index] == '-') index++;
                    start = index;
                    while (index < value.Length && char.IsDigit(value[index])) index++;
                    if (start == index) return false;
                    while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
                }

                if (index < value.Length && value[index] == ':')
                {
                    index++;
                    while (index < value.Length && value[index] != '}')
                    {
                        if (value[index] == '{') return false;
                        index++;
                    }
                }

                if (index >= value.Length || value[index] != '}') return false;
                signature[argumentIndex] = signature.TryGetValue(argumentIndex, out var count) ? count + 1 : 1;
                index++;
            }
            else if (value[index] == '}')
            {
                if (index + 1 < value.Length && value[index + 1] == '}')
                {
                    index += 2;
                    continue;
                }
                return false;
            }
            else
            {
                index++;
            }
        }

        return maxIndex < 0 || signature.Keys.All(argumentIndex => argumentIndex <= maxIndex);
    }

    private sealed class FormatProbe : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "x";
        public override string ToString() => "x";
    }
}
