namespace Kanban.Analysis;

/// <summary>
/// CSV 字段转义——**全仓库唯一实现**（RFC 4180，ADR-4 单源约定）。
/// 历史问题：手写转义曾散落 WorkOrderManagerViewModel/ProductionReviewCsvExportService 两处且完全重复，
/// 后收敛到 MainAPP.Helpers.CsvUtil；WASM 无法引用 MainAPP，又在 ProductionAnalysis.CsvEscape 本地复制
/// （注释自述"暂本地实现"）——本库统一承载，各端委托。
/// 与 CsvHelper（HistoryQueryHelper.BuildCsv / AlarmCsvIOService）的行为保持一致。
/// </summary>
public static class CsvUtil
{
    /// <summary>
    /// CSV 字段转义：字段含逗号/双引号/换行时用双引号包裹，内部双引号加倍（RFC 4180）。
    /// 与 CsvHelper 行为一致。
    /// </summary>
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return string.Empty;
        return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
    }
}
