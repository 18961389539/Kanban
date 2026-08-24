using Kanban.Collector.Core.Models;
using MainAPP.Resources;

namespace MainAPP.Services;

/// <summary>
/// CSV 文件的本地化映射。
/// 导出使用当前 UI 文化；导入同时接受四种内置语言、枚举名称和旧版英文格式。
/// </summary>
internal static class CsvLocalization
{
    private static readonly string[] SupportedCultures = ["zh-CN", "en-US", "ja-JP", "pt-BR"];

    public static string[] HeaderAliases(string resourceKey, string canonicalName)
    {
        var aliases = new List<string>();
        AddAlias(aliases, Strings.S(resourceKey, canonicalName));
        AddAlias(aliases, canonicalName);
        foreach (var culture in SupportedCultures)
            AddAlias(aliases, Strings.GetEmbeddedValue(resourceKey, culture));
        return aliases.ToArray();
    }

    public static string AlarmLevelText(AlarmLevel level) => level switch
    {
        AlarmLevel.High => Strings.Level_High,
        AlarmLevel.Medium => Strings.Level_Medium,
        _ => Strings.Level_Low,
    };

    public static bool TryParseAlarmLevel(string? raw, out AlarmLevel level)
        => TryParseEnum(raw,
            [
                (AlarmLevel.High, "Level_High"),
                (AlarmLevel.Medium, "Level_Medium"),
                (AlarmLevel.Low, "Level_Low"),
            ],
            out level);

    public static string DefectSeverityText(DefectSeverity severity) => severity switch
    {
        DefectSeverity.Critical => Strings.Severity_Critical,
        DefectSeverity.Major => Strings.Severity_Major,
        _ => Strings.Severity_Minor,
    };

    public static bool TryParseDefectSeverity(string? raw, out DefectSeverity severity)
        => TryParseEnum(raw,
            [
                (DefectSeverity.Critical, "Severity_Critical"),
                (DefectSeverity.Major, "Severity_Major"),
                (DefectSeverity.Minor, "Severity_Minor"),
            ],
            out severity);

    public static string DefectCategoryText(DefectCategory category) => category switch
    {
        DefectCategory.Appearance => Strings.Defect_Appearance,
        DefectCategory.Dimension => Strings.Defect_Dimension,
        DefectCategory.Function => Strings.Defect_Function,
        DefectCategory.Packaging => Strings.Defect_Packaging,
        _ => Strings.Defect_Other,
    };

    public static bool TryParseDefectCategory(string? raw, out DefectCategory category)
        => TryParseEnum(raw,
            [
                (DefectCategory.Appearance, "Defect_Appearance"),
                (DefectCategory.Dimension, "Defect_Dimension"),
                (DefectCategory.Function, "Defect_Function"),
                (DefectCategory.Packaging, "Defect_Packaging"),
                (DefectCategory.Other, "Defect_Other"),
            ],
            out category);

    public static string DataSourceValueTypeText(DataSourceValueType type) => type switch
    {
        DataSourceValueType.Int32 => Strings.Dsm_Int32,
        DataSourceValueType.Float32 => Strings.Dsm_Float32,
        DataSourceValueType.Bool => Strings.Dsm_Bool,
        _ => Strings.Dsm_String,
    };

    public static bool TryParseDataSourceValueType(string? raw, out DataSourceValueType type)
        => TryParseEnum(raw,
            [
                (DataSourceValueType.Int32, "Dsm_Int32"),
                (DataSourceValueType.Float32, "Dsm_Float32"),
                (DataSourceValueType.Bool, "Dsm_Bool"),
                (DataSourceValueType.String, "Dsm_String"),
            ],
            out type);

    public static string BooleanText(bool value) => value ? Strings.Dsm_BoolTrue : Strings.Dsm_BoolFalse;

    public static bool TryParseBoolean(string? raw, bool defaultValue, out bool value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = defaultValue;
            return true;
        }

        var text = raw.Trim();
        if (bool.TryParse(text, out value)) return true;
        if (text is "1")
        {
            value = true;
            return true;
        }
        if (text is "0")
        {
            value = false;
            return true;
        }
        if (MatchesLocalized(text, "Dsm_BoolTrue", "True"))
        {
            value = true;
            return true;
        }
        if (MatchesLocalized(text, "Dsm_BoolFalse", "False"))
        {
            value = false;
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryParseRequiredBoolean(string? raw, out bool value)
    {
        value = false;
        return !string.IsNullOrWhiteSpace(raw)
            && TryParseBoolean(raw, false, out value);
    }

    private static bool TryParseEnum<TEnum>(
        string? raw,
        IReadOnlyList<(TEnum Value, string ResourceKey)> options,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        foreach (var option in options)
        {
            if (!MatchesLocalized(text, option.ResourceKey, option.Value.ToString())) continue;
            value = option.Value;
            return true;
        }
        return false;
    }

    private static bool MatchesLocalized(string text, string resourceKey, string canonicalName)
    {
        if (string.Equals(text, canonicalName, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, Strings.S(resourceKey, canonicalName), StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var culture in SupportedCultures)
        {
            var localized = Strings.GetEmbeddedValue(resourceKey, culture);
            if (string.Equals(text, localized, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void AddAlias(List<string> aliases, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        if (!aliases.Any(existing => string.Equals(existing, alias, StringComparison.OrdinalIgnoreCase)))
            aliases.Add(alias);
    }
}