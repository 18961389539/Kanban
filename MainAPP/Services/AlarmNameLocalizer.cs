using System.Globalization;
using Kanban.Collector.Core.Models;

namespace MainAPP.Services;

/// <summary>
/// 报警/缺陷/计数报警名称多语言解析：按 Localization.Apply 捕获的界面语言
/// （与 Strings 同源；不读 CurrentUICulture，避免 WPF UI 线程仍为系统中文）。
/// 缺失时回退主名称。历史事件（AlarmEventRecord 等）是落库快照，不参与翻译——
/// 本解析器仅用于配置对象（Alarm/Defect/CounterAlarm）与实时报警（ActiveAlarmInfo）的显示。
/// </summary>
public static class AlarmNameLocalizer
{
    /// <summary>按当前界面语言选取多语言名称；空/空白回退主名称。</summary>
    public static string Resolve(string fallback, string? en, string? ja, string? pt)
    {
        var localized = Localization.CurrentLanguageCode switch
        {
            "en-US" => en,
            "ja-JP" => ja,
            "pt-BR" => pt,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(localized) ? fallback : localized;
    }

    /// <summary>报警配置对象的多语言名称（编辑/配置展示用）。</summary>
    public static string Resolve(Alarm alarm)
        => Resolve(alarm.Name, alarm.NameEn, alarm.NameJa, alarm.NamePt);

    /// <summary>缺陷配置对象的多语言名称。</summary>
    public static string Resolve(Defect defect)
        => Resolve(defect.Name, defect.NameEn, defect.NameJa, defect.NamePt);

    /// <summary>计数报警配置对象的多语言名称。</summary>
    public static string Resolve(CounterAlarm counterAlarm)
        => Resolve(counterAlarm.Name, counterAlarm.NameEn, counterAlarm.NameJa, counterAlarm.NamePt);

    /// <summary>数据采集源配置对象的多语言名称。</summary>
    public static string Resolve(DataSource source)
        => Resolve(source.Name, source.NameEn, source.NameJa, source.NamePt);

    /// <summary>数据采集值项配置对象的多语言名称。</summary>
    public static string Resolve(DataSourceValue value)
        => Resolve(value.Name, value.NameEn, value.NameJa, value.NamePt);

    /// <summary>数据采集枚举取值映射的多语言显示名。</summary>
    public static string Resolve(DataSourceEnumValue enumValue)
        => Resolve(enumValue.DisplayName, enumValue.DisplayNameEn, enumValue.DisplayNameJa, enumValue.DisplayNamePt);
}
