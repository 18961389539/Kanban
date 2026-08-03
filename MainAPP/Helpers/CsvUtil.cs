namespace MainAPP.Helpers;

/// <summary>
/// CSV 字段转义单源实现（RFC 4180）。
/// 历史问题：手写转义曾散落 WorkOrderManagerViewModel/ProductionReviewCsvExportService 两处且完全重复，
/// 与 CsvHelper（HistoryQueryHelper/AlarmCsvIOService）的行为需保持一致——统一走本类。
/// </summary>
public static class CsvUtil
{
    /// <summary>
    /// CSV 字段转义：字段含逗号/双引号/换行时用双引号包裹，内部双引号加倍（RFC 4180）。
    /// 与 CsvHelper 行为一致（HistoryQueryHelper.BuildCsv / AlarmCsvIOService 走 CsvHelper）。
    /// </summary>
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
    }
}
